<h1 align="center">
  <img src="docs/logo.svg" alt="PackRat: hoards everything, just not on your disk" width="600">
</h1>

PackRat is a systemd service for Fedora that watches one or more
directories and **moves** everything in them to Amazon S3 (or any
S3-compatible storage): each file is uploaded, verified, and then deleted
locally. Paths relative to the watched directory are kept, under an optional
prefix:

```
~/Scans/2026/invoices/acme.pdf
  └─▶ s3://my-bucket/scans/2026/invoices/acme.pdf
      (directory ~/Scans, prefix "scans")
```

It works behind HTTP(S)/SOCKS proxies and follows `no_proxy` properly,
including CIDR ranges, so endpoints you reach through an IPsec tunnel go
direct while everything else uses the proxy.

## Install

Grab the RPM for your Fedora release from the
[releases page](https://github.com/linus-berg/packrat/releases), then:

```sh
sudo dnf install ./packrat-*.fc44.noarch.rpm
```

It depends on Fedora's `dotnet-runtime-10.0` package. To build the RPM
yourself, see [Development](#development).

## Set up

1. Edit **`/etc/packrat/config.json`**: set the bucket and add
   directories. The file is commented; the minimum is:

   ```jsonc
   {
     "bucket": "my-bucket",
     "aws": { "region": "eu-north-1" },
     "directories": [
       { "path": "~/Scans", "prefix": "scans" },
       { "path": "~/Pictures/Export", "bucket": "photos", "prefix": "export" }
     ]
   }
   ```

2. Credentials: by default the service uses the **service user's**
   `~/.aws/config` and `~/.aws/credentials` (set `aws.profile` to pick a
   profile; SSO and assume-role profiles work). Alternatively, put
   `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` in
   `/etc/packrat/environment` (mode 0600).

3. Start it as your user, so it can read and delete your files:

   ```sh
   sudo systemctl enable --now packrat@$USER
   journalctl -u packrat@$USER -f
   ```

   The unit starts at boot whether or not you're logged in. After editing
   the config, `sudo systemctl restart packrat@$USER`. An invalid
   config makes the service exit with status 78 and it is not restarted
   until you fix it.

Try a configuration without touching anything:

```sh
packrat --config /etc/packrat/config.json --dry-run
```

## How it decides a file is safe to move

A file is uploaded and deleted only when **all** of these hold:

- it is a regular file (symlinks are never followed or deleted; FIFOs,
  sockets and devices are skipped);
- it isn't excluded (defaults skip `*.part`, `*.crdownload`, `*.tmp`,
  `*.swp`, `*~`, LibreOffice lock files, GNOME's `.goutputstream-*`, ...);
- it hasn't been modified for `sync.settleTime` (default 30s);
- no process the service can see has it open for writing;
- its size and mtime didn't change between the scan and the upload.

Then it is uploaded (multipart for large files, with the SDK's CRC32
integrity checks), `HEAD`-checked for the right size (`sync.verifyUploads`),
and deleted only if it **still** has the same size and mtime. Anything that
fails stays put and is retried with backoff (30s doubling to 1h).
Subdirectories that have been empty for the settle time are removed; the
watched directory itself never is.

Scans run every `sync.scanInterval` (default 1m), and inotify wakes the
service early when something changes. Set `deleteAfterUpload: false` on a
directory to copy instead of move.

Each object also gets an `mtime` metadata entry (the same format rclone
uses).

## Proxies, `no_proxy` and IPsec tunnels

Proxy settings come from `/etc/packrat/environment` (`https_proxy`,
`http_proxy`, `all_proxy`, `no_proxy`; lowercase wins over uppercase) or
from `proxy.url` / `proxy.noProxy` in the config (the config's `noProxy`
entries are *added* to the environment's). Proxy URLs may include
`user:password@`; keep those in the environment file, which only root can
read.

`no_proxy` entries follow curl/Go conventions, plus a few additions:

| Entry                                | Matches                                        |
| ------------------------------------ | ---------------------------------------------- |
| `*`                                  | everything                                     |
| `example.com`, `.example.com`, `*.example.com` | `example.com` and all its subdomains |
| `10.1.2.3`, `fd00::1`, `[fd00::1]`   | that address                                   |
| `10.0.0.0/8`, `fd00::/8`             | any address in the range                       |
| `minio.lan:9000`, `[fd00::1]:9000`   | only that port                                 |

`localhost` and loopback addresses always go direct.

**IPsec tunnels.** Tunnels route *subnets*, but the S3 endpoint is usually
a *host name*. With `proxy.resolveHostnames` (on by default), host names are
resolved and compared with the IP/CIDR entries: if
`s3.corp.example.com` resolves to `10.20.1.5` and `no_proxy` contains
`10.20.0.0/16`, it goes direct. When a name resolves to several addresses,
the ones inside the tunnelled ranges are tried first, so the connection
really goes through the tunnel. Routing decisions are cached for a minute,
so a tunnel coming up or DNS changing is picked up quickly.

Connections are also tuned for tunnels and proxies: pooled connections are
recycled after 30s idle or 5 minutes in total, and TCP keepalive plus
`TCP_USER_TIMEOUT` drop a stalled connection after 2 minutes (instead of
Linux's ~15) so the upload is retried.

The service logs where each endpoint is routed, and why, so problems show
up in the journal:

```
Proxy: https http://proxy.corp:3128 (from env: https_proxy), http none
no_proxy: localhost, .corp.example.com, 10.20.0.0/16 (host names resolving into IP/CIDR entries go direct)
Route to https://s3.corp.example.com:443: direct (no_proxy entry '10.20.0.0/16' contains 10.20.1.5)
Route to https://my-bucket.s3.eu-north-1.amazonaws.com:443: via proxy http://proxy.corp:3128 (env: https_proxy)
Bucket my-bucket: reachable
```

## AWS permissions

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:PutObject", "s3:GetObject", "s3:AbortMultipartUpload"],
      "Resource": "arn:aws:s3:::my-bucket/scans/*"
    },
    {
      "Effect": "Allow",
      "Action": "s3:ListBucket",
      "Resource": "arn:aws:s3:::my-bucket"
    }
  ]
}
```

`s3:GetObject` is needed for `verifyUploads`; `s3:ListBucket` is optional.
It's used for the startup check and by copy-mode directories to skip files
already in the bucket. A lifecycle rule that aborts incomplete multipart
uploads after a day is a good idea on any bucket.

## Configuration reference

All keys are case-insensitive; durations are written like `500ms`, `30s`,
`5m`, `1h30m` or `00:01:30` (a bare number is rejected). Unknown keys are
errors, so typos don't go unnoticed.

| Key                          | Default         | Notes                                                    |
| ---------------------------- | --------------- | -------------------------------------------------------- |
| `bucket`                     |                 | Default for directories without their own `bucket`.      |
| `aws.profile`                | default chain   | Profile in the service user's `~/.aws`.                  |
| `aws.region`                 | from profile    |                                                          |
| `aws.serviceUrl`             |                 | S3-compatible endpoint, e.g. MinIO.                      |
| `aws.forcePathStyle`         | `false`         | Usually `true` with `serviceUrl`.                        |
| `aws.maxRetries`             | `5`             | Per request, on top of the per-file backoff.             |
| `aws.connectTimeout`         | `30s`           |                                                          |
| `aws.requestTimeout`         | `15m`           |                                                          |
| `aws.checksums`              | `WhenSupported` | `WhenRequired` for old S3-compatible servers.            |
| `proxy.enabled`              | `true`          | `false` = always direct.                                 |
| `proxy.url`                  | from env        | Overrides `https_proxy`/`http_proxy`.                    |
| `proxy.useEnvironment`       | `true`          | Read the proxy environment variables.                    |
| `proxy.noProxy`              | `[]`            | Added to `no_proxy`.                                     |
| `proxy.resolveHostnames`     | `true`          | Match host names against IP/CIDR entries.                |
| `sync.scanInterval`          | `1m`            |                                                          |
| `sync.settleTime`            | `30s`           |                                                          |
| `sync.maxConcurrentUploads`  | `4`             |                                                          |
| `sync.deleteAfterUpload`     | `true`          | Default for directories.                                 |
| `sync.deleteEmptyDirectories`| `true`          |                                                          |
| `sync.verifyUploads`         | `true`          |                                                          |
| `sync.watch`                 | `true`          | inotify; scans still run if watching fails.              |
| `sync.useDefaultExcludes`    | `true`          |                                                          |
| `sync.exclude`               | `[]`            | gitignore-style patterns for every directory.            |
| `sync.storageClass`          |                 | e.g. `STANDARD_IA`, `GLACIER_IR`.                        |
| `directories[].path`         | required        | Absolute or `~/...`; directories may not nest.           |
| `directories[].bucket`       | `bucket`        |                                                          |
| `directories[].prefix`       | none            | Slashes are normalized: `/a//b/` → `a/b`.                |
| `directories[].deleteAfterUpload` | `sync.deleteAfterUpload` |                                             |
| `directories[].storageClass` | `sync.storageClass` |                                                      |
| `directories[].exclude`      | `[]`            | Added to `sync.exclude`.                                 |

The config file is found via `--config`, then `$PACKRAT_CONFIG`,
then `/etc/packrat/config.json`. To give one user a different
config, set `PACKRAT_CONFIG` in
`/etc/packrat/<user>.environment`.

## Development

Requires the .NET 10 SDK (`sudo dnf install dotnet-sdk-10.0`).

```sh
dotnet build
dotnet test
dotnet run --project src/PackRat -- --config ./my-config.json --dry-run --verbose
```

Code style is defined in `PackRat.slnx.DotSettings` (Rider) and
mirrored in `.editorconfig`, so `dotnet build` enforces the naming rules
(`snake_case` locals and parameters, `field_`, `s_field_`, `S_READONLY_`,
`C_CONST`) and CI fails on any violation.

Build the RPMs locally (needs `rpm-build systemd-rpm-macros`):

```sh
packaging/build-rpm.sh          # → artifacts/rpmbuild/{RPMS,SRPMS}
```

CI (`.github/workflows/ci.yml`) runs format, build and tests, then builds,
lints, installs and smoke-tests the RPM on Fedora 43, 44 and 45. Pushing a
tag `vX.Y.Z` publishes the RPMs as a GitHub release. Bump `VersionPrefix`
in `Directory.Build.props` and add a `%changelog` entry in
`packaging/packrat.spec` when you do.

## License

MIT
