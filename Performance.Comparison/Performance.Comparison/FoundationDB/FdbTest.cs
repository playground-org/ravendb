﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FoundationDB.Client;

namespace Performance.Comparison.FoundationDB
{
    public class FdbTest : StoragePerformanceTestBase
    {
        public FdbTest(byte[] buffer)
            : base(buffer)
        {
        }

        public override string StorageName
        {
            get { return "FoundationDB"; }
        }

        private async Task NewDatabaseAsync()
        {
            using (FdbDatabase db = await Fdb.OpenAsync())
            using (IFdbTransaction tx = db.BeginTransaction())
            {
                tx.ClearRange(new FdbKeyRange(FdbKey.MinValue, FdbKey.MaxValue));
                await tx.CommitAsync();
            }
        }

        private void NewDatabase()
        {
            NewDatabaseAsync().Wait();
        }

        public override List<PerformanceRecord> WriteSequential(IEnumerable<TestData> data, PerfTracker perfTracker)
        {
            return Write(string.Format("[FoundationDB] sequential write ({0} items)", Constants.ItemsPerTransaction),
                data,
                Constants.ItemsPerTransaction, Constants.WriteTransactions, perfTracker);
        }

        public override List<PerformanceRecord> WriteParallelSequential(
            IEnumerable<TestData> data, PerfTracker perfTracker, int numberOfThreads, out long elapsedMilliseconds)
        {
            return
                WriteParallel(
                    string.Format("[FoundationDB] parallel sequential write ({0} items)", Constants.ItemsPerTransaction),
                    data,
                    Constants.ItemsPerTransaction, Constants.WriteTransactions, perfTracker, numberOfThreads,
                    out elapsedMilliseconds);
        }

        public override List<PerformanceRecord> WriteRandom(IEnumerable<TestData> data, PerfTracker perfTracker)
        {
            return Write(string.Format("[FoundationDB] random write ({0} items)", Constants.ItemsPerTransaction), data,
                Constants.ItemsPerTransaction, Constants.WriteTransactions, perfTracker);
        }

        public override List<PerformanceRecord> WriteParallelRandom(IEnumerable<TestData> data, PerfTracker perfTracker,
            int numberOfThreads, out long elapsedMilliseconds)
        {
            return
                WriteParallel(
                    string.Format("[FoundationDB] parallel random write ({0} items)", Constants.ItemsPerTransaction),
                    data,
                    Constants.ItemsPerTransaction, Constants.WriteTransactions, perfTracker, numberOfThreads,
                    out elapsedMilliseconds);
        }

        public override PerformanceRecord ReadSequential(PerfTracker perfTracker)
        {
            IEnumerable<int> sequentialIds = Enumerable.Range(0, Constants.ReadItems);

            return Read(string.Format("[FoundationDB] sequential read ({0} items)", Constants.ReadItems), sequentialIds,
                perfTracker);
        }

        public override PerformanceRecord ReadParallelSequential(PerfTracker perfTracker, int numberOfThreads)
        {
            IEnumerable<int> sequentialIds = Enumerable.Range(0, Constants.ReadItems);

            return ReadParallel(
                string.Format("[FoundationDB] parallel sequential read ({0} items)", Constants.ReadItems), sequentialIds,
                perfTracker, numberOfThreads);
        }

        public override PerformanceRecord ReadRandom(IEnumerable<int> randomIds, PerfTracker perfTracker)
        {
            return Read(string.Format("[FoundationDB] random read ({0} items)", Constants.ReadItems), randomIds,
                perfTracker);
        }

        public override PerformanceRecord ReadParallelRandom(IEnumerable<int> randomIds, PerfTracker perfTracker,
            int numberOfThreads)
        {
            return ReadParallel(string.Format("[FoundationDB] parallel random read ({0} items)", Constants.ReadItems),
                randomIds, perfTracker, numberOfThreads);
        }

        private List<PerformanceRecord> Write(string operation, IEnumerable<TestData> data, int itemsPerTransaction,
            int numberOfTransactions, PerfTracker perfTracker)
        {
            return WriteAsync(operation, data, itemsPerTransaction, numberOfTransactions, perfTracker).Result;
        }

        private async Task<List<PerformanceRecord>> WriteAsync(string operation, IEnumerable<TestData> data,
            int itemsPerTransaction,
            int numberOfTransactions, PerfTracker perfTracker)
        {
            NewDatabase();

            using (FdbDatabase db = await Fdb.OpenAsync())
            {
                IEnumerator<TestData> enumerator = data.GetEnumerator();

                return WriteInternal(operation, enumerator, itemsPerTransaction, numberOfTransactions, perfTracker, db);
            }
        }

        private List<PerformanceRecord> WriteParallel(string operation, IEnumerable<TestData> data,
            int itemsPerTransaction, int numberOfTransactions, PerfTracker perfTracker, int numberOfThreads,
            out long elapsedMilliseconds)
        {
            Tuple<List<PerformanceRecord>, long> result
                =
                WriteParallelAsync(operation, data, itemsPerTransaction, numberOfTransactions, perfTracker,
                    numberOfThreads).Result;
            elapsedMilliseconds = result.Item2;
            return result.Item1;
        }

        private async Task<Tuple<List<PerformanceRecord>, long>> WriteParallelAsync(string operation,
            IEnumerable<TestData> data,
            int itemsPerTransaction, int numberOfTransactions, PerfTracker perfTracker, int numberOfThreads)
        {
            NewDatabase();

            using (FdbDatabase db = await Fdb.OpenAsync())
            {
                long elapsedMilliseconds;
                List<PerformanceRecord> records = ExecuteWriteWithParallel(
                    data,
                    numberOfTransactions,
                    itemsPerTransaction,
                    numberOfThreads,
                    (enumerator, itmsPerTransaction, nmbrOfTransactions) =>
                        WriteInternal(operation, enumerator, itmsPerTransaction, nmbrOfTransactions, perfTracker, db),
                    out elapsedMilliseconds);
                return new Tuple<List<PerformanceRecord>, long>(records, elapsedMilliseconds);
            }
        }

        private async Task<List<PerformanceRecord>> WriteInternalAsync(string operation,
            IEnumerator<TestData> enumerator,
            long itemsPerTransaction, long numberOfTransactions, PerfTracker perfTracker, FdbDatabase db)
        {
            var sw = new Stopwatch();
            byte[] valueToWrite = null;
            var records = new List<PerformanceRecord>();

            sw.Restart();
            for (int transactions = 0; transactions < numberOfTransactions; transactions++)
            {
                sw.Restart();
                using (IFdbTransaction tx = db.BeginTransaction())
                {
                    for (int i = 0; i < itemsPerTransaction; i++)
                    {
                        enumerator.MoveNext();

                        valueToWrite = GetValueToWrite(valueToWrite, enumerator.Current.ValueSize);

                        tx.Set(db.Pack(Slice.FromFixed32(enumerator.Current.Id)), Slice.Create(valueToWrite));
                    }

                    await tx.CommitAsync();
                    perfTracker.Record(sw.ElapsedMilliseconds);
                }

                sw.Stop();

                records.Add(new PerformanceRecord
                {
                    Operation = operation,
                    Time = DateTime.Now,
                    Duration = sw.ElapsedMilliseconds,
                    ProcessedItems = itemsPerTransaction
                });
            }

            sw.Stop();

            return records;
        }

        private List<PerformanceRecord> WriteInternal(string operation, IEnumerator<TestData> enumerator,
            long itemsPerTransaction, long numberOfTransactions, PerfTracker perfTracker, FdbDatabase db)
        {
            return
                WriteInternalAsync(operation, enumerator, itemsPerTransaction, numberOfTransactions, perfTracker, db)
                    .Result;
        }

        private PerformanceRecord Read(string operation, IEnumerable<int> ids, PerfTracker perfTracker)
        {
            return ReadAsync(operation, ids, perfTracker).Result;
        }

        private async Task<PerformanceRecord> ReadAsync(string operation, IEnumerable<int> ids, PerfTracker perfTracker)
        {
            Stopwatch sw = Stopwatch.StartNew();

            using (FdbDatabase db = await Fdb.OpenAsync())
            {
                await ReadInternalAsync(ids, perfTracker, db);
            }

            sw.Stop();

            return new PerformanceRecord
            {
                Operation = operation,
                Time = DateTime.Now,
                Duration = sw.ElapsedMilliseconds,
                ProcessedItems = ids.Count()
            };
        }

        private async Task<PerformanceRecord> ReadParallelAsync(string operation, IEnumerable<int> ids,
            PerfTracker perfTracker,
            int numberOfThreads)
        {
            using (FdbDatabase db = await Fdb.OpenAsync())
            {
                return ExecuteReadWithParallel(operation, ids, numberOfThreads,
                    () => ReadInternal(ids, perfTracker, db));
            }
        }

        private PerformanceRecord ReadParallel(string operation, IEnumerable<int> ids, PerfTracker perfTracker,
            int numberOfThreads)
        {
            return ReadParallelAsync(operation, ids, perfTracker, numberOfThreads).Result;
        }

        private static void ReadInternal(IEnumerable<int> ids, PerfTracker perfTracker, FdbDatabase db)
        {
            ReadInternalAsync(ids, perfTracker, db).Wait();
        }

        private static async Task ReadInternalAsync(IEnumerable<int> ids, PerfTracker perfTracker, FdbDatabase db)
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (int id in ids)
            {
                using (var tx = db.BeginTransaction())
                {
                    await tx.GetAsync(db.Pack(Slice.FromFixed32(id)));
                }
            }
            perfTracker.Record(sw.ElapsedMilliseconds);
        }
    }
}