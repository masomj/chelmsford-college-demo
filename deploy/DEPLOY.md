# Deploying the demo to the Hetzner dev server

The demo runs as one Docker container on an existing Hetzner server, behind a Caddy container that already serves another app. Once the one-time setup below is done, **every push to `main` deploys automatically**: GitHub Actions runs the tests, then connects to the server over SSH, updates the code, rebuilds the image on the server and checks `/healthz` and the home page.

Throughout this guide, `<server-ip>` is the server's public IP address (keep the real one out of this public repository).

How the pieces fit:

- `/opt/chelmsford-college-demo` on the server is a clone of this repo. The deploy workflow keeps it at `origin/main`.
- The image is built on the server (`docker compose up -d --build`). Nothing is pushed to a registry.
- The container is called `chelmsford-demo` and listens on port 8080. It publishes no ports on the host. Instead it joins the Docker network of the compose project that runs Caddy, so Caddy can reach it as `chelmsford-demo:8080`.
- The whole site is behind HTTP basic auth, set in Caddy. Only Stripe's webhook (`/pay/webhook/stripe`), the health check (`/healthz`) and the access report (`/reports/*`, which has its own basic auth in the app) are outside it.
- Databases, data-protection keys and Umbraco media live on two named volumes: `chelmsford-college-demo_data` (`/app/data`) and `chelmsford-college-demo_media` (`/app/media`).
- The college's logos, photos and course data are not in git. They sit in `/opt/chelmsford-college-demo/college-content` on the server and are mounted read-only into the container at `/app/college-content`.

All server commands below are run on the server as a user who can run Docker (`root` unless you set up the deploy user in the last section). Laptop commands are PowerShell, run from the root of this repository.

## One-time setup

### 1. Check the server's SSH host key

Work out the server's host key fingerprint now, because the deploy workflow needs it (step 9) and your laptop may need its `known_hosts` entry refreshed if the server was rebuilt.

First read the fingerprint from the Hetzner Cloud console (open the server's console, then run this inside it):

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Then, from the laptop, get the fingerprint the server presents over the network:

```bash
ssh-keyscan -t ed25519 <server-ip> | ssh-keygen -lf -
```

The output looks like `256 SHA256:abc123... <server-ip> (ED25519)`. The two fingerprints must match. If they do not, stop: something is between you and the server.

If your laptop already has an old entry for the server that no longer matches (expected after a rebuild) and the fingerprints above do match, replace it:

```powershell
# laptop
ssh-keygen -R <server-ip>
ssh root@<server-ip> "echo connected"
```

Accept the new key when asked.

### 2. Create the GitHub repository

Create a **public** repository called `masomj/chelmsford-college-demo` and push `main` to it. The deploy clones it over HTTPS without credentials, so it must be public (or the server needs its own read access). Because it is public, never commit `.env`, keys, passwords or the server's IP address.

### 3. DNS

Add an A record in the DNS for `vanguarddigitalsolutions.co.uk`:

| Type | Name | Value | TTL |
|---|---|---|---|
| A | `chelmsford` | `<server-ip>` | 300 (or the default) |

Check it before going on (it can take a few minutes):

```powershell
# laptop
Resolve-DnsName chelmsford.vanguarddigitalsolutions.co.uk -Type A
```

### 4. Clone the repo on the server and create `.env`

```bash
git clone https://github.com/masomj/chelmsford-college-demo.git /opt/chelmsford-college-demo
cd /opt/chelmsford-college-demo
cp .env.example .env
chmod 600 .env
nano .env
```

In `.env`, set at least:

- `Umbraco__CMS__Unattended__UnattendedUserEmail` and `Umbraco__CMS__Unattended__UnattendedUserPassword`: the back-office admin account created on first start. Use a long random password (`openssl rand -base64 24`).
- `Umbraco__CMS__Imaging__HMACSecretKey`: the secret that signs image URLs. Generate it with `openssl rand -base64 64` and make sure it ends up on one line. Never commit it.
- `Payments__FinanceEmail`: where duplicate-payment refund alerts go.
- `Payments__AllowLiveKeys`: leave as `false`. Live Stripe keys are refused unless this is `true`.
- `CADDY_NETWORK`: the Docker network Caddy is on (found in the next step).

Leave the Stripe values empty for now (see "Stripe test mode" below).

### 5. Check the Caddy network name

```bash
docker network ls
docker ps --format '{{.Names}}\t{{.Image}}'      # find the Caddy container name
docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' <caddy-container>
```

The last command prints the network(s) the Caddy container is on. Put that name in `CADDY_NETWORK` in `.env`.

### 6. Copy the college content from the laptop

First make sure `college-content` is up to date on the laptop (see the README: `python tools/export-demo-data.py` and `python tools/prepare-images.py`). Then:

```powershell
# laptop
scp -r .\college-content root@<server-ip>:/opt/chelmsford-college-demo/
```

On the server, check it arrived and make it readable by the container's user:

```bash
ls /opt/chelmsford-college-demo/college-content/images | head
ls /opt/chelmsford-college-demo/college-content/source-data
chmod -R a+rX /opt/chelmsford-college-demo/college-content
```

`college-content` is gitignored, so `git reset --hard` during deploys leaves it alone.

### 7. Add the site to Caddy, with a password

Caddy runs as a container in a separate compose stack (the other app's compose folder, called `<caddy-compose-folder>` below). It reads a `Caddyfile` in that folder, bind-mounted read-only to `/etc/caddy/Caddyfile`.

**Important:** if the other app's deploy workflow copies its `Caddyfile` from its own repository to the server on every push, a block added only on the server will be overwritten by its next deploy. So:

1. Add the block from [`Caddyfile.snippet`](Caddyfile.snippet) to the end of the `Caddyfile` in the other app's repository and commit it there. Do this with the real bcrypt hash from the steps below, not the placeholder.
2. Either let that app's next deploy copy it, or put the same block into the `Caddyfile` in `<caddy-compose-folder>` on the server now (so the two match).

**Generate the password hash on the server.** The whole demo is behind basic auth, and the tender panel gets the username and password. Pick a long random password, then hash it with Caddy itself:

```bash
cd <caddy-compose-folder>
docker compose exec caddy caddy hash-password --plaintext 'the-password-for-the-panel'
```

(Without `--plaintext` it prompts for the password twice instead, which keeps it out of your shell history.) The output is a bcrypt hash starting `$2a$14$`. In the block, replace `REPLACE_WITH_BCRYPT_HASH_FROM_CADDY_HASH_PASSWORD` with that hash, and change the username `panel` if you want a different one. Never put the plain password in the Caddyfile or in git. Send the username and password to the tender panel separately from the URL.

The block protects everything except `/pay/webhook/stripe` (Stripe cannot log in; the webhook is verified by its signature), `/healthz` (used by the deploy workflow) and `/reports/*` (the app protects the access report with its own basic auth). The security headers, including `X-Robots-Tag: noindex`, are kept.

Then validate and reload:

```bash
cd <caddy-compose-folder>
nano Caddyfile        # paste the block at the end
docker compose exec caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
```

If the reload doesn't pick up the change (single-file bind mounts can keep showing the old file when an editor replaces it rather than writing in place), run `docker compose restart caddy` instead. That causes a few seconds of downtime for the other app.

Until the container in the next step is running, Caddy answers the new host name with a 502. That is expected.

### 8. First start

```bash
cd /opt/chelmsford-college-demo
docker compose up -d --build
docker compose logs -f chelmsford-demo     # Ctrl+C to stop following
```

The first build takes a few minutes. The first start runs Umbraco's unattended install, builds the college source database from `college-content/source-data`, syncs courses and seeds sample content, so allow a minute or two. `/healthz` returns 503 until all of that has finished, then 200. Then:

```bash
docker compose ps                                          # STATUS should say (healthy)
docker compose exec chelmsford-demo curl -fsS http://localhost:8080/healthz
docker compose exec -T chelmsford-demo curl -fsS -o /dev/null http://localhost:8080/ && echo "home page ok"
curl -fsS https://chelmsford.vanguarddigitalsolutions.co.uk/healthz
curl -sS -o /dev/null -w '%{http_code}\n' https://chelmsford.vanguarddigitalsolutions.co.uk/                    # expect 401
curl -sS -o /dev/null -w '%{http_code}\n' -u 'panel:the-password-for-the-panel' https://chelmsford.vanguarddigitalsolutions.co.uk/   # expect 200
```

Sign in to the back office at https://chelmsford.vanguarddigitalsolutions.co.uk/umbraco with the email and password from `.env` (after the basic auth prompt).

The unattended settings only matter on the first start (when `/app/data` is empty). After that, change the password in the back office, not in `.env`.

### 9. Turn on automatic deploys (GitHub secrets)

The workflow [`.github/workflows/deploy-dev.yml`](../.github/workflows/deploy-dev.yml) needs three repository secrets:

| Secret | What goes in it |
|---|---|
| `HETZNER_DEV_HOST` | `<server-ip>` |
| `HETZNER_SSH_KEY` | The full private key (including the `-----BEGIN` and `-----END` lines) of a key that can log in to the server as the deploy user |
| `HETZNER_HOST_FINGERPRINT` | The server's SSH host key fingerprint, the full `SHA256:...` string. The deploy refuses to connect if the server presents a different key |

The deploy job uses a GitHub environment called `dev`, which GitHub creates on the first run. Repository secrets are visible to it, so add them as repository secrets (Settings, Secrets and variables, Actions).

**Host key fingerprint.** Get it from the laptop and compare it with the Hetzner console, as in step 1:

```bash
ssh-keyscan -t ed25519 <server-ip> | ssh-keygen -lf -
```

The value to store is the `SHA256:...` part of the output (for example `SHA256:abc123...`). In the Hetzner Cloud console, run `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` and check it is the same string before you trust it.

**Deploy key.** Use a dedicated deploy key rather than your personal key, so it can be revoked on its own. The commands below log in as `root`, which is what the workflow uses by default. To use a non-root user instead, follow "Optional: a non-root deploy user" first and swap the user name in. On the laptop:

```powershell
# laptop: create a key pair. Press Enter twice when asked for a passphrase
# (leave it empty: GitHub Actions can't type one).
ssh-keygen -t ed25519 -f "$env:USERPROFILE\.ssh\chelmsford_deploy" -C "github-actions chelmsford-college-demo"

# allow it to log in to the server
Get-Content "$env:USERPROFILE\.ssh\chelmsford_deploy.pub" | ssh root@<server-ip> "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"

# check it works on its own
ssh -i "$env:USERPROFILE\.ssh\chelmsford_deploy" -o IdentitiesOnly=yes root@<server-ip> "echo deploy key ok"

# store the secrets (GitHub CLI), or paste them in the web UI
Get-Content -Raw "$env:USERPROFILE\.ssh\chelmsford_deploy" | gh secret set HETZNER_SSH_KEY --repo masomj/chelmsford-college-demo
gh secret set HETZNER_DEV_HOST --repo masomj/chelmsford-college-demo --body "<server-ip>"
gh secret set HETZNER_HOST_FINGERPRINT --repo masomj/chelmsford-college-demo --body "SHA256:paste-the-fingerprint-here"
```

Keep the private key file somewhere safe, or delete it once it is stored in GitHub. To revoke it later, remove the line ending `github-actions chelmsford-college-demo` from `~/.ssh/authorized_keys` of the deploy user on the server.

The workflow pins `appleboy/ssh-action` to a full commit SHA (the commit behind tag `v1.0.3`), so a moved tag can't change what runs with your server credentials. To update it, look up the new tag's commit with `git ls-remote https://github.com/appleboy/ssh-action refs/tags/<tag>` (if the tag is annotated, also check `refs/tags/<tag>^{}` and use the commit SHA), then replace the SHA and the trailing comment.

From now on, every push to `main` deploys automatically. Pushes that only change Markdown files, `deploy/Caddyfile.snippet` or `.env.example` are skipped. You can also run the workflow by hand from the Actions tab ("Deploy to Dev", "Run workflow").

The deploy waits up to four minutes for `/healthz` to return 200 (it returns 503 while Umbraco is still starting or seeding), then checks that the home page answers from inside the container, because the public home page is behind basic auth. If either check fails, the job prints `docker compose ps` and the last 200 log lines, and fails.

## Stakeholder access and tracking

The tender panel gets one shared login for the site and one for the CMS. The real passwords are kept in a private guide, never in this repository.

- **Site password (Caddy basic auth).** The Caddy block has one user, `panel`. Hash the shared site password on the server with `docker compose exec caddy caddy hash-password` (run in `<caddy-compose-folder>`, see step 7) and paste the hash into the block in place of the placeholder. Caddy passes the signed-in user name to the app in the `X-Remote-User` header, and removes any `X-Remote-User` a visitor tries to send.
- **CMS account.** Set `Panel__CmsUser__Name`, `Panel__CmsUser__Email` and `Panel__CmsUser__Password` in the server's `.env`. This is a shared reviewer account in the Editor group for the back office at `/umbraco`.
- **Access report.** Set `Reports__UserName` and `Reports__Password` in `.env`. These protect `/reports/access` with the app's own basic auth, which is why Caddy does not apply its basic auth to `/reports/*`. Use a different password from the site one.
- **Notifications.** Set `Access__NotifyEmail` to the address that should be emailed when the panel first visits each day or signs in, and fill in the `Smtp__*` values (for example Gmail with an app password) so the email can be sent.
- **Where to see visits.** Open `https://chelmsford.vanguarddigitalsolutions.co.uk/reports/access` and sign in with the `Reports__*` credentials.

After changing `.env`, apply it with `docker compose up -d` in `/opt/chelmsford-college-demo`.

## Optional: a non-root deploy user

The workflow logs in as `root` by default. Safer is a dedicated user that is only in the `docker` group. Be aware that membership of the `docker` group is still effectively root access on that machine, so this limits what a leaked key can do over SSH (no direct root shell, no changing system files), not what it can do through Docker. Keep the key dedicated to this one job.

On the server, as root:

```bash
adduser --disabled-password --gecos "" deploy
usermod -aG docker deploy
mkdir -p /home/deploy/.ssh && chmod 700 /home/deploy/.ssh
# paste the deploy key's PUBLIC key (the .pub file) into this file:
nano /home/deploy/.ssh/authorized_keys
chmod 600 /home/deploy/.ssh/authorized_keys
chown -R deploy:deploy /home/deploy/.ssh
# let the deploy user own the checkout
chown -R deploy:deploy /opt/chelmsford-college-demo
```

Check it from the laptop (`ssh -i "$env:USERPROFILE\.ssh\chelmsford_deploy" -o IdentitiesOnly=yes deploy@<server-ip> "docker ps"`), then change `SERVER_USER: root` to `SERVER_USER: deploy` in `.github/workflows/deploy-dev.yml`. Once the deploys work, remove the deploy key from `/root/.ssh/authorized_keys`.

## Stripe test mode (optional)

With the three `Payments__Stripe__*` values empty, the site uses its built-in test checkout, which is clearly labelled and moves no money. To show Stripe's real hosted checkout in test mode:

1. In the Stripe dashboard, switch to **Test mode** and copy the test keys (Developers, API keys): `pk_test_...` and `sk_test_...`.
2. Add a webhook endpoint (Developers, Webhooks, Add endpoint):
   - URL: `https://chelmsford.vanguarddigitalsolutions.co.uk/pay/webhook/stripe`
   - Event: `checkout.session.completed`
   - Copy its signing secret (`whsec_...`).
3. On the server, put the three values in `.env`:

   ```bash
   cd /opt/chelmsford-college-demo
   nano .env    # Payments__Stripe__SecretKey, __PublishableKey, __WebhookSecret
   docker compose up -d     # recreates the container with the new environment
   ```

The webhook URL must stay outside the basic auth, which the Caddy block already does. Never use live keys on the demo: they are refused unless `Payments__AllowLiveKeys=true`, which should stay `false`. Test card: `4242 4242 4242 4242`, any future expiry date, any CVC.

## Updating

Push to `main`. The workflow tests, deploys and checks health. Watch it in the repo's Actions tab.

To deploy by hand on the server (for example if Actions is unavailable):

```bash
cd /opt/chelmsford-college-demo
git fetch --prune origin && git reset --hard origin/main
docker compose up -d --build --remove-orphans
docker image prune -f
docker compose ps
```

After changing `college-content` on the laptop, copy it again (step 6) and restart the container: `docker compose restart chelmsford-demo`.

## Logs and checks

```bash
cd /opt/chelmsford-college-demo
docker compose ps
docker compose logs --tail=200 chelmsford-demo
docker compose logs -f chelmsford-demo
curl -sS -o /dev/null -w '%{http_code}\n' https://chelmsford.vanguarddigitalsolutions.co.uk/healthz     # 200 when ready, 503 while starting
```

## Backups

Everything that changes at run time is in the two volumes. To back them up to `/opt/chelmsford-college-demo/backups`:

```bash
cd /opt/chelmsford-college-demo
mkdir -p backups
docker compose stop chelmsford-demo      # SQLite files are only consistent when the app is stopped
for v in data media; do
  docker run --rm -v chelmsford-college-demo_$v:/vol:ro -v "$PWD/backups":/backup alpine \
    tar czf /backup/$v-$(date +%Y%m%d-%H%M).tar.gz -C /vol .
done
docker compose start chelmsford-demo
ls -lh backups
```

`backups/` is gitignored and deploys leave it alone, but it is still on the same server, so copy archives off the server if they matter:

```powershell
# laptop
scp -r root@<server-ip>:/opt/chelmsford-college-demo/backups .\backups
```

To restore (this replaces the current data):

```bash
cd /opt/chelmsford-college-demo
docker compose stop chelmsford-demo
docker run --rm -v chelmsford-college-demo_data:/vol -v "$PWD/backups":/backup alpine \
  sh -c 'rm -rf /vol/* && tar xzf /backup/data-YYYYMMDD-HHMM.tar.gz -C /vol'
docker run --rm -v chelmsford-college-demo_media:/vol -v "$PWD/backups":/backup alpine \
  sh -c 'rm -rf /vol/* && tar xzf /backup/media-YYYYMMDD-HHMM.tar.gz -C /vol'
docker compose start chelmsford-demo
```

## Resetting the demo

To go back to a fresh install (new database, new admin user from `.env`, freshly seeded content, all test payments and bookings gone):

```bash
cd /opt/chelmsford-college-demo
docker compose down
docker volume rm chelmsford-college-demo_data chelmsford-college-demo_media
docker compose up -d
docker compose logs -f chelmsford-demo
```

`college-content` is untouched by a reset.

## Removing the demo

```bash
cd /opt/chelmsford-college-demo
docker compose down -v --rmi local
```

Then remove the `chelmsford.vanguarddigitalsolutions.co.uk` block from the Caddyfile (in the other app's repository and on the server) and reload Caddy, delete the DNS record, and delete the three GitHub secrets.
