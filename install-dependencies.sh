./tools/dotnet-install.sh --channel 6.0
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools' >> ~/.bashrc

dotnet tool install -g dotnet-format
./install-pre-commit.sh