﻿using System.Linq;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents.Indexes;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_10504 : RavenTestBase
    {
        private class Document
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        private class DocumentIndex : AbstractIndexCreationTask<Document>
        {
            public class Result
            {
                public string Name { get; set; }
            }

            public DocumentIndex()
            {
                Map = doc => from documents in doc
                             select new Result
                             {
                                 Name = documents.Name
                             };
            }
        }

        [Fact]
        public async Task CanSelectIdField()
        {
            using (var store = GetDocumentStore())
            {
                new DocumentIndex().Execute(store);

                var doc = new Document
                {
                    Id = "myDocuments/123",
                    Name = "document"
                };

                using (var session = store.OpenSession())
                {
                    session.Store(doc);
                    session.Advanced.WaitForIndexesAfterSaveChanges(indexes: new[] { nameof(DocumentIndex) });
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var docs = session.Advanced.DocumentQuery<Document, DocumentIndex>()
                        .WhereEquals(x => x.Name, "document")
                        // this would work
                        //.SelectFields<Document>("Id", "Name")
                        .SelectFields<Document>("Id")
                        .ToList();

                    Assert.Equal(1, docs.Count);
                    Assert.NotNull(docs[0]);
                    Assert.Equal(doc.Id, docs[0].Id);
                    Assert.Null(docs[0].Name);
                }

                using (var session = store.OpenAsyncSession())
                {
                    var docs = await session.Advanced.AsyncDocumentQuery<Document, DocumentIndex>()
                        .WhereEquals(x => x.Name, "document")
                        // this would work
                        //.SelectFields<Document>("Id", "Name")
                        .SelectFields<Document>("Id")
                        .ToListAsync();

                    Assert.Equal(1, docs.Count);
                    Assert.NotNull(docs[0]);
                    Assert.Equal(doc.Id, docs[0].Id);
                    Assert.Null(docs[0].Name);
                }
            }
        }
    }
}
