if ($PSVersionTable.Platform -eq 'Unix') {
   throw 'This Powershell script is not supported on Linux, please run instal-pre-commit.sh'
}

if(-not (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
   throw 'Since script will install python and a pip, it cannot run in a non-privileged terminal.'
}

#If chocolatey doesn't exist, install it
$testchoco = powershell choco -v
if(-not($testchoco)){
   Write-Host 'Installing Choco..'
   Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
}

choco install python
choco install git

pip install virtualenv==20.0.33
pip install pre-commit
pre-commit install

if (!(Test-Path '.pre-commit-config.yaml')){
New-Item .pre-commit-config.yaml
Set-Content .pre-commit-config.yaml 'repos:
- repo: local
  hooks:
  #Use dotnet format already installed on your machine
  - id: dotnet-format
    name: dotnet-format
    language: system
    entry: dotnet format --include
    types_or: ["c#", "vb"]
- repo: https://github.com/pre-commit/pre-commit-hooks
  rev: v4.3.0
  hooks:
  - id: check-yaml
  - id: check-json
  - id: check-xml
  - id: fix-byte-order-marker
  - id: mixed-line-ending
  - id: trailing-whitespace
    args: [--markdown-linebreak-ext=md]
  - id: pretty-format-json
    args: ['--autofix']
- repo: https://github.com/compilerla/conventional-pre-commit
  rev: v2.0.0
  hooks:
    - id: conventional-pre-commit
      stages: [commit-msg]
      args: [] # optional: list of Conventional Commits types to allow e.g. [feat, fix, ci, chore, test]'
}
git add .pre-commit-config.yaml

pre-commit install --hook-type commit-msg
pre-commit run --all-files