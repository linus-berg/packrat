#!/usr/bin/env bash
# Builds the source and binary RPMs into artifacts/rpmbuild.
# Needs: rpm-build dotnet-sdk-10.0 systemd-rpm-macros tar gzip
#
#   packaging/build-rpm.sh                     # version from Directory.Build.props
#   PACKRAT_VERSION=1.2.0 PACKRAT_RELEASE=1 packaging/build-rpm.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name=packrat

dotnet_version="${PACKRAT_VERSION:-$(sed -n \
  's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' \
  "$root/Directory.Build.props")}"
rpm_version="${dotnet_version//-/\~}"
release="${PACKRAT_RELEASE:-1}"
topdir="${RPM_TOPDIR:-$root/artifacts/rpmbuild}"

mkdir -p "$topdir"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}

tar --create --gzip \
  --file "$topdir/SOURCES/$name-$rpm_version.tar.gz" \
  --directory "$root" \
  --exclude-vcs \
  --exclude=./artifacts \
  --exclude=./.idea \
  --exclude=bin \
  --exclude=obj \
  --exclude=TestResults \
  --transform "s,^\.,$name-$rpm_version," \
  .

rpmbuild -ba "$root/packaging/$name.spec" \
  --define "_topdir $topdir" \
  --define "packrat_version $rpm_version" \
  --define "packrat_dotnet_version $dotnet_version" \
  --define "packrat_release $release"

find "$topdir/RPMS" "$topdir/SRPMS" -name '*.rpm' -print
