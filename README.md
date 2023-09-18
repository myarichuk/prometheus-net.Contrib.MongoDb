# Library.Template

This is a template for a .Net library project. The main idea is to provide convenient preconfigured project structure that would utilize [Github Flow](https://docs.github.com/en/get-started/quickstart/github-flow) for development process and [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for semantic versioning and generating a change log.

## Features

* GitHub Actions
  * When PR is opened, run tests and lint commit messages for [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) standards
  * When PR is merged, run tests, publish to NuGet, update the changelog file and create GitHub release while using [GitVersion](https://gitversion.net/) to figure out the next version. GitVersion is configured to use Conventional Commits to figure out the next release version.
* Preconfigured [StyleCop](https://github.com/StyleCop/StyleCop) rules
* [Pre-Commit](https://pre-commit.com/) configuration and scripts to install the tool and it's hooks
* Scripts to install dependencies so development can be started quicker

## How to start development with this template?

1. Create a GitHub repository with this project as it's tempalte
2. Execute ``install-dependencies`` script (if on Windows execute PowerShell script, on Linux execute the Bash script). This will ensure there is correct .Net SDK installed, install dotnet-format tool and execute pre-commit install script
3. In order for ``nuget.org`` deployment to work, create [NuGet API key](https://docs.microsoft.com/en-us/nuget/nuget-org/publish-a-package#create-api-keys) and set it in the repository ``secrets`` as ``NUGET_TOKEN``.
4. Replace 'COMPANY-PLACEHOLDER' in ``stylecop.json`` to have a correct value
5. Replace 'COMPANY-PLACEHOLDER' values in ``Directory.Build.props`` file to proper values
