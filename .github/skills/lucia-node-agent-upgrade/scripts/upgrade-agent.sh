set -e
A=/usr/lib/lucia/agent
systemctl stop lucia-node-agent
rm -rf $A.prev; cp -a $A $A.prev
tar -xzf /tmp/managed-node-agent.tar.gz -C $A --no-same-owner
chown -R root:root $A; find $A -type d -exec chmod 700 {} +; find $A -type f -exec chmod 600 {} +; chmod 700 $A/lucia-node-agent $A/createdump
systemctl start lucia-node-agent
sleep 20; systemctl is-active lucia-node-agent
