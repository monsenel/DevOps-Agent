#!/bin/sh
set -eu

app_uid="${DVA_UID:-1654}"
app_gid="${DVA_GID:-1654}"

case "$app_uid:$app_gid" in
    *[!0-9:]*|:*)
        echo "DVA_UID and DVA_GID must be numeric values." >&2
        exit 1
        ;;
esac

mkdir -p /data/keys
chown "$app_uid:$app_gid" /data /data/keys
if [ -e /data/devops-agent.db ]; then
    chown "$app_uid:$app_gid" /data/devops-agent.db
fi

exec setpriv --reuid="$app_uid" --regid="$app_gid" --clear-groups dotnet DevOpsAgent.Web.dll
