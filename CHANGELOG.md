# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v0.8.6.0] - 2023-09-27
### :bug: Bug Fixes
- [`b19b972`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/b19b972f098ae2633efae621b9c73d8343de210b) - rewrite (fix) open cursor duration metric

### :white_check_mark: Tests
- [`f6bc217`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/f6bc21770be1c506c5fd06d5d563a47e9bd1e497) - tests for open cursor duration metric


## [v0.8.4.0] - 2023-09-27
### :bug: Bug Fixes
- [`3febfaf`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/3febfaf994546230f5c2f9746db940b1e1632948) - reimplement cursor document count metric (so it will be grouped by operationId) - it should work properly now!


## [v0.8.2.0] - 2023-09-26
### :bug: Bug Fixes
- [`bb46ea6`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/bb46ea62f369001a85365a9248e2e730652dc92a) - cursor metrics should *properly* work now


## [v0.8.0.0] - 2023-09-26
### :sparkles: New Features
- [`fb6b61d`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/fb6b61d92f300fae250c090d67b3dfe29a662b18) - add mongodb_client_command_duration_summary and adjust buckets of the command duration

### :bug: Bug Fixes
- [`ae397de`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/ae397de29140a51153fcdb4f122e856ebb8931c8) - open cursor duration and batch size metrics (properly detect cursor Id - *facepalm*)

### :wrench: Chores
- [`f70b02e`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/f70b02ef912d6c72a0ecfa368202f9f3ceac2e30) - minor refactoring + fix metric names in readme


## [v0.7.8.0] - 2023-09-26
### :bug: Bug Fixes
- [`bb36fa6`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/bb36fa6e8fec6f6f50eb27a51f7be22eecc062db) - gracefully handle isMaster command


## [v0.7.6.0] - 2023-09-26
### :bug: Bug Fixes
- [`8cdebc0`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/8cdebc0a38228ead15218ad894d5d4e72cea461c) - make sure 'isMaster' command won't cause unhandled exceptions in the host process


## [v0.7.4.0] - 2023-09-26
### :wrench: Chores
- [`6c70586`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/6c70586139bc68af50a2ff75cdd7329b7901bd04) - add configuration to instrumentation method *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.7.2.0] - 2023-09-26
### :wrench: Chores
- [`7b3fb36`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/7b3fb36e3ea5bfd73c284a00f7853ee07f5481f4) - add performance considerations to readme *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.7.0.0] - 2023-09-26
### :sparkles: New Features
- [`bb439da`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/bb439daa00b23091761b870d7a90172f0208b0bd) - add command request size metric + do some refactoring *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.6.0.0] - 2023-09-25
### :sparkles: New Features
- [`ea8fb5c`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/ea8fb5cf0635373ab8ac2d2ad9d8e4cb1cecc033) - add query count provider (to better understand activity spikes, SELECT N+1 and other fun issues) *(commit by [@myarichuk](https://github.com/myarichuk))*

### :white_check_mark: Tests
- [`71acf31`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/71acf31905943b4020bc507fea29af3f355ddf2b) - add tests for query count metric *(commit by [@myarichuk](https://github.com/myarichuk))*

### :wrench: Chores
- [`bc87465`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/bc87465bfec4619d90b1a3aedb923a3c9add4e76) - adjust readme to add new Query Count metric *(commit by [@myarichuk](https://github.com/myarichuk))*


## [v0.5.5.0] - 2023-09-25
### :bug: Bug Fixes
- [`2410a68`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/2410a68b7454177411e0ffa68486279382fdcc1d) - try to take care of race condition in a test *(commit by [@myarichuk](https://github.com/myarichuk))*

### :white_check_mark: Tests
- [`c85c611`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/c85c611d6b62873a609c2801daf2363999758221) - "migrate" tests to use MongoTestContext - a more structured way to write tests *(commit by [@myarichuk](https://github.com/myarichuk))*

### :wrench: Chores
- [`6f6dffe`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/6f6dffee6e3c29da77688b9e956456f9bd3cb45b) - adjust readme file (add a "motivation" section) *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`1275b8d`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/1275b8d217622a062b1d76a26d4764c08dd0d3ea) - **deps**: bump Microsoft.NET.Test.Sdk from 17.6.0 to 17.7.2 *(commit by [@dependabot[bot]](https://github.com/apps/dependabot))*
- [`999ede2`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/999ede20cf32b5cc2b9d0996a09a63cad4857594) - **deps**: bump coverlet.collector from 3.2.0 to 6.0.0 *(commit by [@dependabot[bot]](https://github.com/apps/dependabot))*
- [`dd66b48`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/dd66b48f5452bb04352c2553e3add23d37a798b9) - **deps**: bump xunit.runner.visualstudio from 2.4.5 to 2.5.1 *(commit by [@dependabot[bot]](https://github.com/apps/dependabot))*
- [`3beb686`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/3beb686ce3f2edb3d11189f73a308e3f97ed9945) - fix tests so they work properly *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`baf2a63`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/baf2a639d16a965ef9eb105e9d585527eeed04ca) - **tests**: remove unnecessary code *(commit by [@myarichuk](https://github.com/myarichuk))*
- [`9d6d392`](https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/commit/9d6d3925b94d4210573771e69bdd37c16601129e) - minor code change *(commit by [@myarichuk](https://github.com/myarichuk))*


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
[v0.5.5.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.5.0.0...v0.5.5.0
[v0.6.0.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.5.5.0...v0.6.0.0
[v0.7.0.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.6.0.0...v0.7.0.0
[v0.7.2.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.7.0.0...v0.7.2.0
[v0.7.4.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.7.2.0...v0.7.4.0
[v0.7.6.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.7.4.0...v0.7.6.0
[v0.7.8.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.7.6.0...v0.7.8.0
[v0.8.0.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.7.10.0...v0.8.0.0
[v0.8.2.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.8.0.0...v0.8.2.0
[v0.8.4.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.8.2.0...v0.8.4.0
[v0.8.6.0]: https://github.com/myarichuk/prometheus-net.Contrib.MongoDb/compare/v0.8.4.0...v0.8.6.0