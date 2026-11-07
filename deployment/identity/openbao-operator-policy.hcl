path "sys/health" {
  capabilities = ["read"]
}

path "sys/audit" {
  capabilities = ["read", "sudo"]
}

path "sys/mounts" {
  capabilities = ["read"]
}

path "sys/mounts/lucia-secrets" {
  capabilities = ["create", "read", "update", "sudo"]
}

path "sys/mounts/lucia-secrets/*" {
  capabilities = ["read", "update", "sudo"]
}

path "sys/auth" {
  capabilities = ["read"]
}

path "sys/auth/lucia-*" {
  capabilities = ["create", "read", "update", "delete", "sudo"]
}

path "sys/auth/openbao-backup" {
  capabilities = ["create", "read", "update", "delete", "sudo"]
}

path "sys/policies/acl/lucia-*" {
  capabilities = ["create", "read", "update", "delete"]
}

path "auth/lucia-*" {
  capabilities = ["create", "read", "update", "delete", "list", "sudo"]
}

path "auth/openbao-backup/*" {
  capabilities = ["create", "read", "update", "delete", "list", "sudo"]
}

path "auth/token/create" {
  capabilities = ["update"]
}

path "auth/token/revoke-self" {
  capabilities = ["update"]
}

path "auth/token/revoke-accessor" {
  capabilities = ["update"]
}

path "sys/storage/raft/snapshot" {
  capabilities = ["read", "update"]
}

path "sys/storage/raft/snapshot-force" {
  capabilities = ["update"]
}

path "lucia-secrets/*" {
  capabilities = ["create", "read", "update", "delete", "list", "patch"]
}

path "sys/raw/*" {
  capabilities = ["deny"]
}

path "sys/generate-root/*" {
  capabilities = ["deny"]
}
