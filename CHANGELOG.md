# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v0.5.0.0] - 2023-09-24
### :sparkles: New Features
- [`dfc51c2`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/dfc51c2f815983d5e22191fe926722116915d30f) - add connection metrics provider *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`ac9368c`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/ac9368c00bbc38dc2ab91837d9714913003d221b) - query filter size metric (for those nasty queries with 2k IN clauses) *(commit by [@myarichuk](https://github.com/myarichuk))*

### :white_check_mark: Tests
- [`b53e246`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/b53e2463b30421056ac04ac4747fd6b2d42518b9) - add tests for that, of course *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`741fa6b`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/741fa6b1703451748c48c4b617acca6c2ce5302d) - add tests for query filter size metric *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`3984864`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/398486417b8cb88df40acbf8491c9b90331ebd8e) - introduce MongoTestContext that would abstract away repeated code for setup and tearing down test MongoDb instances *(commit by [@myarichuk](https://github.com/myarichuk))*

### :wrench: Chores
- [`883aaea`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/883aaeaaf335c3b03bb25911c488be30fafff62b) - fix typos and add new metrics to readme *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`50e56d6`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/50e56d669c074ac1910de1804e9b34c210d8c542) - remove unnecessary code (code quality) *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`62273ae`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/62273aee6ca68d449477c97bd792a40925efbcc2) - minor rename *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`81adff4`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/81adff4bd2122d9bbaca9c2f0744fa7bfc194167) - minor fixes (make sure RegisterAll() for metric provider happens only once ever) *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`240882e`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/240882e32cab2060cdcf550a1adff83117860c07) - adjust readme file for the new metric *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`ed0e68b`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/ed0e68ba50f6069eb2338bef8c53a35fccb26200) - try to enhance ConnectionMetricsTests (not sure why it fails on Linux) *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`f8c9d09`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/f8c9d0910f5753ad4178cb14f37dcf80235b908e) - add some debugging code to tests *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.3.1.0] - 2023-09-23
### :wrench: Chores
- [`7fd9a5f`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/7fd9a5f4acd96d5b83ebce291ecadd168cad7bf8) - minor adjustment to readme file (proper nuget package Id) *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.2.6.0] - 2023-09-23
### :bug: Bug Fixes
- [`b6c42cd`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/b6c42cd83d672314446db23030caf3601ed6214f) - now readme embedding in nuget should *properly* work *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.2.4.0] - 2023-09-23
### :bug: Bug Fixes
- [`35ec428`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/35ec4287a1d7b651c611469b70f1581fe73c671f) - nuget readme package error *(commit by [@myarichuk](https://github.com/myarichuk))*

### :wrench: Chores
- [`6b9039d`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/6b9039db59a9aeb2d5661de86e301904e94bc3d0) - adjust Github Workflow and add README link to NuGet nuspec *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`488dabc`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/488dabc36f05c374716bc343314c6a65949aebf4) - add command_type to command response size metric *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`f757195`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/f7571957a683933dbbc00aac2470500f82616621) - adjust README file to provide accurate metric data *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.1.4.0] - 2023-09-19
### :bug: Bug Fixes
- [`67fa8d2`](https://github.com/myarichuk/prometheus-net.MongoDb/commit/67fa8d2945da22aeefc22545efbe441c78181cfe) - change the way changelog.md is comitted


## [v0.0.1.0] - 2023-09-19
### :sparkles: New Features
- [`ec00ddf`](https://github.com/myarichuk/prometheus-net.MongoDb/commit/ec00ddfce9f47f9fb277a87bb72852113e3dd690) - initial commit *(commit by [@myarichuk](https://github.com/myarichuk))*
[v0.1.4.0]: https://github.com/myarichuk/prometheus-net.MongoDb/compare/v0.1.3.0...v0.1.4.0
[v0.2.4.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.2.2.0...v0.2.4.0
[v0.2.6.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.2.4.0...v0.2.6.0
[v0.3.1.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.3.0.0...v0.3.1.0
[v0.5.0.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.3.1.0...v0.5.0.0