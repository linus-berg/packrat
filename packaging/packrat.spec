# Built by packaging/build-rpm.sh (locally) and .github/workflows/ci.yml.
# Pass --define "packrat_version X" / "packrat_release Y" to override the defaults.
%global debug_package %{nil}
%global dotnet_version 10.0
%global app_dir %{_prefix}/lib/%{name}

%{!?packrat_version: %global packrat_version 0.1.0}
%{!?packrat_release: %global packrat_release 1}
# NuGet/.NET versions may contain '-', RPM versions use '~' instead.
%{!?packrat_dotnet_version: %global packrat_dotnet_version %{packrat_version}}

Name:           packrat
Version:        %{packrat_version}
Release:        %{packrat_release}%{?dist}
Summary:        Hoards your files in S3 so your disk does not have to
License:        MIT
URL:            https://github.com/linus-berg/packrat
Source0:        %{name}-%{version}.tar.gz

BuildArch:      noarch
BuildRequires:  dotnet-sdk-%{dotnet_version}
BuildRequires:  systemd-rpm-macros
Requires:       dotnet-runtime-%{dotnet_version}
%{?systemd_requires}

%description
PackRat is a systemd service that watches one or more directories and
uploads every file in them to Amazon S3 (or S3-compatible storage), keeping
the path relative to the watched directory under an optional key prefix.
Once an upload is verified the local file is deleted.

It honors http_proxy/https_proxy/no_proxy, including CIDR ranges, and can
match host names by their resolved addresses so endpoints reached through
an IPsec tunnel bypass the proxy.

%prep
%autosetup -n %{name}-%{version}

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
       DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet publish src/PackRat/PackRat.csproj \
  --configuration Release \
  --output publish \
  --no-self-contained \
  -p:UseAppHost=false \
  -p:Version=%{packrat_dotnet_version} \
  -p:ContinuousIntegrationBuild=true

%install
install -d %{buildroot}%{app_dir}
cp -a publish/. %{buildroot}%{app_dir}/
# Linux only: drop Windows-only assets, and some NuGet DLLs ship as 0744.
rm -rf %{buildroot}%{app_dir}/runtimes/win*
find %{buildroot}%{app_dir} -type d -empty -delete
find %{buildroot}%{app_dir} -type f -exec chmod 0644 {} +
install -Dpm 0755 packaging/packrat.sh %{buildroot}%{_bindir}/%{name}
install -Dpm 0644 packaging/packrat@.service \
  %{buildroot}%{_unitdir}/%{name}@.service
install -Dpm 0644 packaging/config.json \
  %{buildroot}%{_sysconfdir}/%{name}/config.json
install -Dpm 0600 packaging/environment \
  %{buildroot}%{_sysconfdir}/%{name}/environment

%post
%systemd_post %{name}@.service

%preun
%systemd_preun %{name}@*.service

%postun
%systemd_postun_with_restart %{name}@*.service

%files
%license LICENSE
%doc README.md
%{_bindir}/%{name}
%{app_dir}/
%{_unitdir}/%{name}@.service
%dir %{_sysconfdir}/%{name}
%config(noreplace) %{_sysconfdir}/%{name}/config.json
%config(noreplace) %attr(0600,root,root) %{_sysconfdir}/%{name}/environment

%changelog
* Sat Nov 14 2026 Linus Berg <linus@fenix.me.uk> - 0.1.0-1
- First release: hoards everything you give it, just not on your disk.
