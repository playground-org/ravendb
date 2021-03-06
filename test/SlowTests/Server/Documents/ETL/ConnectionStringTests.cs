﻿using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.SQL;
using Raven.Client.ServerWide;
using Raven.Server.ServerWide.Context;
using SlowTests.Server.Documents.ETL.SQL;
using Xunit;

namespace SlowTests.Server.Documents.ETL
{
    public class ConnectionStringTests : EtlTestBase
    {
        [Fact]
        public void CanAddAndRemoveConnectionStrings()
        {
            using (var store = GetDocumentStore())
            {
                var ravenConnectionString = new RavenConnectionString()
                {
                    Name = "RavenConnectionString",
                    TopologyDiscoveryUrls = new[] { "http://localhost:8080" },
                    Database = "Northwind",
                };
                store.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(ravenConnectionString));

                var sqlConnectionString = new SqlConnectionString
                {
                    Name = "SqlConnectionString",
                    ConnectionString = SqlEtlTests.GetConnectionString(store),
                };

                store.Maintenance.Send(new PutConnectionStringOperation<SqlConnectionString>(sqlConnectionString));

                DatabaseRecord record;
                using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    record =  Server.ServerStore.Cluster.ReadDatabase(context, store.Database);
                }

                Assert.True(record.RavenConnectionStrings.ContainsKey("RavenConnectionString"));
                Assert.Equal(ravenConnectionString.Name , record.RavenConnectionStrings["RavenConnectionString"].Name);
                Assert.Equal(ravenConnectionString.TopologyDiscoveryUrls, record.RavenConnectionStrings["RavenConnectionString"].TopologyDiscoveryUrls);
                Assert.Equal(ravenConnectionString.Database, record.RavenConnectionStrings["RavenConnectionString"].Database);

                Assert.True(record.SqlConnectionStrings.ContainsKey("SqlConnectionString"));
                Assert.Equal(sqlConnectionString.Name, record.SqlConnectionStrings["SqlConnectionString"].Name);
                Assert.Equal(sqlConnectionString.ConnectionString, record.SqlConnectionStrings["SqlConnectionString"].ConnectionString);

                store.Maintenance.Send(new RemoveConnectionStringOperation<RavenConnectionString>(ravenConnectionString));
                store.Maintenance.Send(new RemoveConnectionStringOperation<SqlConnectionString>(sqlConnectionString));

                using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    record = Server.ServerStore.Cluster.ReadDatabase(context, store.Database);
                }

                Assert.False(record.RavenConnectionStrings.ContainsKey("RavenConnectionString"));
                Assert.False(record.SqlConnectionStrings.ContainsKey("SqlConnectionString"));

            }
        }

        [Fact]
        public void CanUpdateConnectionStrings()
        {
            using (var store = GetDocumentStore())
            {
                var ravenConnectionString = new RavenConnectionString()
                {
                    Name = "RavenConnectionString",
                    TopologyDiscoveryUrls = new[]{"http://127.0.0.1:8080" },
                    Database = "Northwind",
                };
                store.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(ravenConnectionString));

                var sqlConnectionString = new SqlConnectionString
                {
                    Name = "SqlConnectionString",
                    ConnectionString = SqlEtlTests.GetConnectionString(store),
                };

                store.Maintenance.Send(new PutConnectionStringOperation<SqlConnectionString>(sqlConnectionString));

                //update url
                ravenConnectionString.TopologyDiscoveryUrls = new[]{"http://127.0.0.1:8081"};
                store.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(ravenConnectionString));
                
                //update name : need to remove the old entry
                store.Maintenance.Send(new RemoveConnectionStringOperation<SqlConnectionString>(sqlConnectionString));
                sqlConnectionString.Name = "New-Name";
                store.Maintenance.Send(new PutConnectionStringOperation<SqlConnectionString>(sqlConnectionString));

                DatabaseRecord record;
                using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    record = Server.ServerStore.Cluster.ReadDatabase(context, store.Database);
                }

                Assert.True(record.RavenConnectionStrings.ContainsKey("RavenConnectionString"));
                Assert.Equal("http://127.0.0.1:8081", record.RavenConnectionStrings["RavenConnectionString"].TopologyDiscoveryUrls.First());

                Assert.False(record.SqlConnectionStrings.ContainsKey("SqlConnectionString"));
                Assert.True(record.SqlConnectionStrings.ContainsKey("New-Name"));
                Assert.Equal(sqlConnectionString.ConnectionString, record.SqlConnectionStrings["New-Name"].ConnectionString);
            }
        }

        [Fact]
        public void CanGetAllConnectionStrings()
        {
            using (var store = GetDocumentStore())
            {
                var ravenConnectionStrings = new List<RavenConnectionString>();
                var sqlConnectionStrings = new List<SqlConnectionString>();
                for (var i = 0; i < 5; i++)
                {
                    var ravenConnectionStr = new RavenConnectionString()
                    {
                        Name = $"RavenConnectionString{i}",
                        TopologyDiscoveryUrls = new[] { $"http://127.0.0.1:808{i}" },
                        Database = "Northwind",
                    };
                    var sqlConnectionStr = new SqlConnectionString
                    {
                        Name = $"SqlConnectionString{i}",
                        ConnectionString = SqlEtlTests.GetConnectionString(store)
                    };

                    ravenConnectionStrings.Add(ravenConnectionStr);
                    sqlConnectionStrings.Add(sqlConnectionStr);

                    store.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(ravenConnectionStr));
                    store.Maintenance.Send(new PutConnectionStringOperation<SqlConnectionString>(sqlConnectionStr));
                }

                var result = store.Maintenance.Send(new GetConnectionStringsOperation());
                Assert.NotNull(result.SqlConnectionStrings);
                Assert.NotNull(result.RavenConnectionStrings);

                for (var i = 0; i < 5; i++)
                {
                    result.SqlConnectionStrings.TryGetValue($"SqlConnectionString{i}", out var sql);
                    Assert.Equal(sql?.ConnectionString, sqlConnectionStrings[i].ConnectionString);

                    result.RavenConnectionStrings.TryGetValue($"RavenConnectionString{i}", out var raven);
                    Assert.Equal(raven?.TopologyDiscoveryUrls, ravenConnectionStrings[i].TopologyDiscoveryUrls);
                    Assert.Equal(raven?.Database, ravenConnectionStrings[i].Database);
                }
            }
        }

        [Fact]
        public void CanGetConnectionStringByName()
        {
            using (var store = GetDocumentStore())
            {
                var ravenConnectionStrings = new List<RavenConnectionString>();
                var sqlConnectionStrings = new List<SqlConnectionString>();
                
                var ravenConnectionStr = new RavenConnectionString()
                {
                    Name = "RavenConnectionString",
                    TopologyDiscoveryUrls = new[] { "http://127.0.0.1:8080" },
                    Database = "Northwind",
                };
                var sqlConnectionStr = new SqlConnectionString
                {
                    Name = "SqlConnectionString",
                    ConnectionString = SqlEtlTests.GetConnectionString(store)
                };

                ravenConnectionStrings.Add(ravenConnectionStr);
                sqlConnectionStrings.Add(sqlConnectionStr);

                store.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(ravenConnectionStr));
                store.Maintenance.Send(new PutConnectionStringOperation<SqlConnectionString>(sqlConnectionStr));
                

                var result = store.Maintenance.Send(new GetConnectionStringsOperation(connectionStringName: sqlConnectionStr.Name, type: sqlConnectionStr.Type));
                Assert.True(result.SqlConnectionStrings.Count > 0);
                Assert.True(result.RavenConnectionStrings.Count == 0);
            }
        }
    }
}
