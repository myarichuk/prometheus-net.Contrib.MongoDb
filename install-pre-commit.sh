sudo apt update
sudo apt -y install build-essential zlib1g-dev \
   libncurses5-dev libgdbm-dev libnss3-dev \
   libssl-dev libreadline-dev libffi-dev curl

sudo apt-get -y install python3.8 pip git

pip install virtualenv==20.0.33
pip install pre-commit
pre-commit install

touch .pre-commit-config.yaml
echo 'repos:
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
      args: [''--autofix'']
  - repo: https://github.com/compilerla/conventional-pre-commit
    rev: v2.0.0
    hooks:
      - id: conventional-pre-commit
        stages: [commit-msg]
        args: [] # optional: list of Conventional Commits types to allow e.g. [feat, fix, ci, chore, test]
' > .pre-commit-config.yaml

git add .pre-commit-config.yaml

pre-commit install --hook-type commit-msg
pre-commit run --all-files