using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PrometheusNet.MongoDb.Tests;

[CollectionDefinition("NonConcurrentCollection", DisableParallelization = true)]
public class NonConcurrentCollection { }
