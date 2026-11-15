#!/usr/bin/env bash
# dva-testlab.sh — disposable multi-server lab for testing DevOps Agent locally.
# Servers have fixed addresses, so restarts never shuffle IPs:
#   public 10.88.10.0/24, private 10.88.20.0/24, egress 10.88.30.0/24
#   srv-edge 10.88.10.10 / 10.88.20.10, srv-app 10.88.20.20, srv-db 10.88.20.30, srv-search 10.88.20.40
#
# Simulates 4 Linux "servers" as Docker containers:
#   srv-edge   public + private network, SSH on 127.0.0.1:2201 (the jump host), nginx, fail2ban
#   srv-app    private only, runs its own Docker (DinD): api, web, two workers
#   srv-db     private only, PostgreSQL + Redis as plain services (not containers)
#   srv-search private only, NOT meant to be in the inventory -> shows up as an Unknown node
# plus an on-demand "attacker" container for SSH brute-force tests.
#
# Nothing here touches real servers. Everything lives in $PWD/dva-testlab-fixed-data (override with $LAB_DIR).
#
# Usage:
#   ./dva-testlab.sh up            build + start, print connection details
#   ./dva-testlab.sh info          print connection details again
#   ./dva-testlab.sh status        container state
#   ./dva-testlab.sh alert-down    stop the "web" container inside srv-app  (alert should open)
#   ./dva-testlab.sh alert-up      start it again                          (alert should resolve)
#   ./dva-testlab.sh attack        60 failed SSH logins against srv-edge from the attacker container
#   ./dva-testlab.sh unban         clear fail2ban bans on srv-edge
#   ./dva-testlab.sh shell <srv>   open a shell inside a lab server (srv-edge, srv-app, srv-db, srv-search)
#   ./dva-testlab.sh down          stop and delete everything (containers, volumes, networks)
#   Run it from a directory outside this repo (or set LAB_DIR), so lab data and the generated
#   SSH key never land in the repo.
#
# Requirements: Docker with the compose plugin, ssh-keygen, ssh-keyscan. Linux or WSL2/macOS Docker Desktop.

set -euo pipefail

LAB_DIR="${LAB_DIR:-$PWD/dva-testlab-fixed-data}"
KEY_NAME="dva_test_ed25519"
EDGE_SSH_PORT="${EDGE_SSH_PORT:-2201}"
EDGE_HTTP_PORT="${EDGE_HTTP_PORT:-8088}"
COMPOSE=(docker compose -f "$LAB_DIR/docker-compose.yml" -p dva-testlab)

log()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "missing dependency: $1"; }

write_files() {
  mkdir -p "$LAB_DIR"/{image,lab,keys}

  # ---------- SSH key for the lab (used by DevOps Agent) ----------
  if [[ ! -f "$LAB_DIR/keys/$KEY_NAME" ]]; then
    log "Generating lab SSH key"
    ssh-keygen -q -t ed25519 -N '' -C 'dva-testlab' -f "$LAB_DIR/keys/$KEY_NAME"
  fi
  cp "$LAB_DIR/keys/$KEY_NAME.pub" "$LAB_DIR/image/authorized_keys"

  # ---------- one image for all servers ----------
  cat > "$LAB_DIR/image/Dockerfile" <<'EOF'
FROM debian:12
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      openssh-server sudo iproute2 iputils-ping curl ca-certificates procps rsyslog \
      nginx fail2ban iptables postgresql redis-server docker.io python3 \
      netcat-openbsd conntrack util-linux less \
 && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash dva \
 && usermod -aG docker dva \
 && echo 'dva ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/dva && chmod 440 /etc/sudoers.d/dva \
 && mkdir -p /run/sshd /run/fail2ban /home/dva/.ssh && chmod 700 /home/dva/.ssh \
 && sed -ri 's/^#?PasswordAuthentication .*/PasswordAuthentication no/; s/^#?PermitRootLogin .*/PermitRootLogin no/' /etc/ssh/sshd_config
COPY authorized_keys /home/dva/.ssh/authorized_keys
RUN chown -R dva:dva /home/dva/.ssh && chmod 600 /home/dva/.ssh/authorized_keys
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
EOF

  cat > "$LAB_DIR/image/entrypoint.sh" <<'EOF'
#!/bin/bash
set -u
# Unique host keys per container (the image is shared).
if [ ! -f /etc/ssh/.dva-hostkeys ]; then
  rm -f /etc/ssh/ssh_host_*; ssh-keygen -A >/dev/null; touch /etc/ssh/.dva-hostkeys
fi
touch /var/log/auth.log
rsyslogd 2>/dev/null || true
/usr/sbin/sshd

case "${ROLE:-}" in
  edge)
    rm -f /etc/nginx/sites-enabled/default
    cp /lab/nginx-demo.conf /etc/nginx/conf.d/demo.conf
    until getent hosts srv-app >/dev/null; do sleep 1; done
    nginx
    cp /lab/jail.local /etc/fail2ban/jail.local
    fail2ban-client -x start >/dev/null 2>&1 || echo "fail2ban failed to start" >&2
    # steady traffic so nginx -> backend connections are observable
    ( while true; do
        curl -s -o /dev/null -H 'Host: api.test.local' http://127.0.0.1/
        curl -s -o /dev/null -H 'Host: app.test.local' http://127.0.0.1/
        sleep 3
      done ) &
    ;;
  app)
    dockerd >/var/log/dockerd.log 2>&1 &
    for _ in $(seq 1 60); do docker info >/dev/null 2>&1 && break; sleep 1; done
    DB_IP=10.88.20.30   # fixed (see compose networks); never resolved at start-up
    docker rm -f api web worker-pg worker-redis >/dev/null 2>&1 || true
    L="--label com.docker.compose.project=demo"
    docker run -d --restart unless-stopped --name api $L --label com.docker.compose.service=api -p 5000:80 traefik/whoami >/dev/null
    docker run -d --restart unless-stopped --name web $L --label com.docker.compose.service=web -p 3000:80 traefik/whoami >/dev/null
    docker run -d --restart unless-stopped --name worker-pg $L --label com.docker.compose.service=worker-pg \
      --add-host "srv-db:$DB_IP" -e PGPASSWORD=app postgres:16-alpine \
      psql -h srv-db -U app -d app -c 'select pg_sleep(1000000)' >/dev/null
    docker run -d --restart unless-stopped --name worker-redis $L --label com.docker.compose.service=worker-redis \
      --add-host "srv-db:$DB_IP" redis:7-alpine redis-cli -h srv-db subscribe dva-demo >/dev/null
    # long-lived connection to a server that is not in the inventory -> Unknown node
    ( while true; do
        exec 3<>/dev/tcp/srv-search/9200 2>/dev/null && sleep 600; exec 3>&- 2>/dev/null; sleep 5
      done ) &
    ;;
  db)
    PGV=$(ls /etc/postgresql | head -1); PGC=/etc/postgresql/$PGV/main
    if ! grep -q 'dva-lab' "$PGC/postgresql.conf"; then
      echo "listen_addresses = '*'  # dva-lab" >> "$PGC/postgresql.conf"
      echo "host all all 0.0.0.0/0 scram-sha-256" >> "$PGC/pg_hba.conf"
    fi
    pg_ctlcluster "$PGV" main start
    if ! su postgres -c "psql -tAc \"select 1 from pg_roles where rolname='app'\"" | grep -q 1; then
      su postgres -c "psql -c \"create user app password 'app'\" -c \"create database app owner app\""
    fi
    redis-server --bind 0.0.0.0 --protected-mode no --daemonize yes
    ;;
  search)
    ( cd /tmp && python3 -m http.server 9200 >/dev/null 2>&1 ) &
    ;;
esac

exec tail -F /var/log/auth.log
EOF

  # ---------- nginx on srv-edge ----------
  cat > "$LAB_DIR/lab/nginx-demo.conf" <<'EOF'
upstream api_backend {
    server srv-app:5000;
    keepalive 8;
}

server {
    listen 80;
    server_name api.test.local;
    location / {
        proxy_pass http://api_backend;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
    }
}

server {
    listen 80 default_server;
    server_name app.test.local;
    location / {
        proxy_pass http://srv-app:3000;
    }
}
EOF

  # ---------- fail2ban on srv-edge ----------
  cat > "$LAB_DIR/lab/jail.local" <<'EOF'
[DEFAULT]
banaction = iptables-multiport
backend = auto

[sshd]
enabled  = true
port     = ssh
backend  = auto
logpath  = /var/log/auth.log
maxretry = 5
findtime = 10m
bantime  = 1h
EOF

  # ---------- compose ----------
  cat > "$LAB_DIR/docker-compose.yml" <<EOF
x-server: &server
  build: ./image
  image: dva-testlab-server
  restart: unless-stopped
  volumes:
    - ./lab:/lab:ro

services:
  srv-edge:
    <<: *server
    container_name: dva-srv-edge
    hostname: srv-edge
    environment: [ROLE=edge]
    cap_add: [NET_ADMIN]
    ports:
      - "127.0.0.1:${EDGE_SSH_PORT}:22"
      - "127.0.0.1:${EDGE_HTTP_PORT}:80"
    networks:
      public: { ipv4_address: 10.88.10.10 }
      private: { ipv4_address: 10.88.20.10 }
    depends_on: [srv-app]

  srv-app:
    <<: *server
    container_name: dva-srv-app
    hostname: srv-app
    environment: [ROLE=app]
    privileged: true            # needed for Docker-in-Docker; lab only
    volumes:
      - ./lab:/lab:ro
      - app-docker:/var/lib/docker
    networks:                   # egress only so the inner Docker can pull images
      private: { ipv4_address: 10.88.20.20 }
      egress: { ipv4_address: 10.88.30.20 }
    depends_on: [srv-db, srv-search]

  srv-db:
    <<: *server
    container_name: dva-srv-db
    hostname: srv-db
    environment: [ROLE=db]
    networks:
      private: { ipv4_address: 10.88.20.30 }

  srv-search:
    <<: *server
    container_name: dva-srv-search
    hostname: srv-search
    environment: [ROLE=search]
    networks:
      private: { ipv4_address: 10.88.20.40 }

  attacker:
    image: alpine:3.20
    profiles: [attack]
    networks: [public]
    command:
      - sh
      - -c
      - >-
        apk add -q --no-cache openssh-client &&
        for i in \$\$(seq 1 60); do ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=3 "admin\$\$i@srv-edge" true 2>/dev/null; done;
        echo "attack finished"

networks:
  public:
    ipam: { config: [ { subnet: 10.88.10.0/24 } ] }
  private:
    internal: true
    ipam: { config: [ { subnet: 10.88.20.0/24 } ] }
  egress:
    ipam: { config: [ { subnet: 10.88.30.0/24 } ] }

volumes:
  app-docker: {}
EOF
}

ip_of() {
  docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{if eq $k "dva-testlab_private"}}{{$v.IPAddress}}{{end}}{{end}}' "$1" 2>/dev/null || true
}

fingerprint_of() {
  docker exec "$1" ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null | awk '{print $2}'
}

info() {
  local key="$LAB_DIR/keys/$KEY_NAME"
  cat <<EOF

==================== DevOps Agent test lab ====================

SSH private key (copy into the folder DevOps Agent reads keys from, e.g. ./keys):
  $key

Add these servers in DevOps Agent (user: dva, key: $KEY_NAME, group: staging):

  Name        Host / port            Jump server   Private IP         Host key (ED25519)
  srv-edge    127.0.0.1 : $EDGE_SSH_PORT       —             $(ip_of dva-srv-edge)   $(fingerprint_of dva-srv-edge)
  srv-app     srv-app   : 22         srv-edge      $(ip_of dva-srv-app)   $(fingerprint_of dva-srv-app)
  srv-db      srv-db    : 22         srv-edge      $(ip_of dva-srv-db)   $(fingerprint_of dva-srv-db)

  Do NOT add srv-search ($(ip_of dva-srv-search)) at first: it must appear as an Unknown node (J2).

If DevOps Agent itself runs in Docker, use host.docker.internal instead of 127.0.0.1
(on Linux add: extra_hosts: ["host.docker.internal:host-gateway"] to its service).

nginx domains on srv-edge (for Trace / J3):
  api.test.local -> upstream api_backend -> srv-app:5000 -> container "api"
  app.test.local -> srv-app:3000 -> container "web"
  Try it: curl -H 'Host: api.test.local' http://127.0.0.1:$EDGE_HTTP_PORT/

Expected observed connections:
  srv-edge nginx  -> srv-app:5000 (keepalive) and :3000
  worker-pg       -> srv-db:5432
  worker-redis    -> srv-db:6379
  srv-app         -> srv-search:9200 (Unknown node)

Test commands:
  ./dva-testlab.sh alert-down / alert-up   container alert open / resolve (T5.3, T5.4)
  ./dva-testlab.sh attack                  60 failed SSH logins -> fail2ban + alert (T6.4, J5)
  ./dva-testlab.sh unban                   clear bans afterwards

ssh_config for the "Import ssh config" test (T1.3):

Host srv-edge
  HostName 127.0.0.1
  Port $EDGE_SSH_PORT
  User dva
  IdentityFile $key
Host srv-app
  HostName srv-app
  User dva
  ProxyJump srv-edge
  IdentityFile $key
Host srv-db
  HostName srv-db
  User dva
  ProxyJump srv-edge
  IdentityFile $key

Manual check:  ssh -F /dev/null -i $key -p $EDGE_SSH_PORT dva@127.0.0.1 'hostname; ip -br addr'
================================================================
EOF
}

case "${1:-}" in
  up)
    need docker; need ssh-keygen
    docker compose version >/dev/null 2>&1 || die "docker compose plugin not found"
    write_files
    log "Building and starting the lab (first run pulls Debian and builds the image)"
    "${COMPOSE[@]}" up -d --build
    log "Waiting for the inner Docker on srv-app to start its containers"
    for _ in $(seq 1 120); do
      if docker exec dva-srv-app docker ps --format '{{.Names}}' 2>/dev/null | grep -q worker-redis; then break; fi
      sleep 2
    done
    docker exec dva-srv-app docker ps --format '  {{.Names}}\t{{.Status}}' 2>/dev/null || warn "inner containers not up yet; check: docker logs dva-srv-app"
    info
    ;;
  info)    info ;;
  status)
    "${COMPOSE[@]}" ps
    echo; echo "Containers inside srv-app:"
    docker exec dva-srv-app docker ps -a --format '  {{.Names}}\t{{.Status}}' 2>/dev/null || true
    ;;
  alert-down) docker exec dva-srv-app docker stop web >/dev/null && log "web stopped inside srv-app — alert should open within ~2 min" ;;
  alert-up)   docker exec dva-srv-app docker start web >/dev/null && log "web started — alert should resolve" ;;
  attack)
    log "Running 60 failed SSH logins against srv-edge from the attacker container"
    "${COMPOSE[@]}" --profile attack run --rm attacker
    docker exec dva-srv-edge fail2ban-client status sshd || true
    ;;
  unban)  docker exec dva-srv-edge fail2ban-client unban --all ;;
  shell)
    [[ -n "${2:-}" ]] || die "usage: $0 shell <srv-edge|srv-app|srv-db|srv-search>"
    docker exec -it "dva-$2" bash
    ;;
  down)
    [[ -f "$LAB_DIR/docker-compose.yml" ]] || die "no lab found in $LAB_DIR"
    "${COMPOSE[@]}" --profile attack down -v --remove-orphans
    log "Lab removed. Key and files remain in $LAB_DIR (delete the folder to reset)."
    ;;
  *)
    sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
