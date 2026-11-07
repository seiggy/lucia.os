#!/usr/bin/env python3
"""Synthetic checks for isolation, immutable recovery mounts and narrow owner requests."""
import json
import unittest
from unittest.mock import patch
import openbao_stack as stack
from provision import Provisioner
import tempfile
from pathlib import Path


class StackChecks(unittest.TestCase):
    def test_identity_deploy_refuses_an_orphaned_configured_vault(self):
        with tempfile.TemporaryDirectory() as folder:
            instance = Provisioner.__new__(Provisioner)
            instance.state = Path(folder)
            instance.settings = {"ports": {"authentik": 9443, "ldaps": 8636, "ca": 9000}}
            (instance.state / "openbao-settings.json").write_text('{"schema_version":1,"bind_address":"127.0.0.1","port":8200}')
            (instance.state / "deployment").mkdir()
            (instance.state / "deployment/docker-compose.yaml").write_text("services:\n  identity-ldap: {}\n")
            with self.assertRaisesRegex(RuntimeError, "omits the configured vault"):
                instance.require_openbao_in_deployment()
            (instance.state / "openbao").mkdir()
            (instance.state / "openbao/stack.json").write_text('{"schemaVersion":1,"project":"lucia-openbao"}')
            instance.require_openbao_in_deployment()
            (instance.state / "openbao/stack.json").write_text('{"schemaVersion":1,"project":"wrong"}')
            with self.assertRaises(ValueError):
                instance.require_openbao_in_deployment()

    def test_compose_reuses_existing_state_without_identity_project_ownership(self):
        value = stack.compose("identity_aspire", 1000, 1000, 8200)
        service = value["services"]["openbao"]
        self.assertEqual(value["name"], "lucia-openbao")
        self.assertEqual(service["image"], stack.IMAGE)
        self.assertEqual(service["container_name"], stack.CONTAINER)
        self.assertEqual(service["networks"]["identity"]["aliases"], ["identity-openbao"])
        self.assertTrue(value["networks"]["identity"]["external"])
        self.assertEqual(service["ports"], ["127.0.0.1:8200:8200"])
        self.assertTrue(service["read_only"])
        self.assertEqual(service["cap_drop"], ["ALL"])
        for mount in service["volumes"]:
            self.assertFalse(mount["bind"]["create_host_path"])
        self.assertNotIn("init", json.dumps(service["command"]))
        self.assertNotIn("unseal", json.dumps(service))

    def test_request_refuses_arbitrary_compose_credentials_and_destructive_actions(self):
        valid = {"schemaVersion": 1, "generation": 4, "desired": "running"}
        self.assertEqual(stack.request(valid), valid)
        for change in [{"desired": "removed"}, {"desired": "restart"}, {"generation": True},
                       {"generation": -1}, {"schemaVersion": True}, {"image": "attacker/image"},
                       {"token": "not-accepted"}, {"unseal": "not-accepted"}]:
            with self.assertRaises(ValueError):
                stack.request({**valid, **change})

    def test_container_collision_refuses_adoption(self):
        class Result:
            returncode = 0
            stdout = json.dumps([{"Config": {"Labels": {"com.docker.compose.project": "identity"}}}])
        with patch.object(stack.subprocess, "run", return_value=Result()):
            with self.assertRaisesRegex(RuntimeError, "another Compose project"):
                stack.inspect()

    def test_sealed_health_does_not_restart_or_initialize(self):
        desired = {"schemaVersion": 1, "generation": 4, "desired": "running"}
        with patch.object(stack, "read", return_value=desired), patch.object(stack.Path, "exists", return_value=True), \
                patch.object(stack, "inspect", return_value={"State": {"Running": True}}), \
                patch.object(stack, "health", return_value={"initialized": True, "sealed": True}), \
                patch.object(stack, "docker_compose") as compose, patch.object(stack, "report") as report:
            stack.tick()
            compose.assert_not_called()
            self.assertEqual(report.call_args.args[1], "sealed")

    def test_missing_container_is_reported_without_silent_recreation(self):
        desired = {"schemaVersion": 1, "generation": 4, "desired": "running"}
        with patch.object(stack, "read", return_value=desired), patch.object(stack.Path, "exists", return_value=True), \
                patch.object(stack, "inspect", return_value=None), patch.object(stack, "docker_compose") as compose, \
                patch.object(stack, "report") as report:
            stack.tick()
            compose.assert_not_called()
            self.assertEqual(report.call_args.args[1], "missing")


if __name__ == "__main__":
    unittest.main()
