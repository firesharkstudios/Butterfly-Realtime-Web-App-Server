﻿/*
 * Copyright 2017 Fireshark Studios, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Nito.AsyncEx;
using NLog;

using Butterfly.Database.Event;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Database.Dynamic {
    /// <summary>
    /// Represents a collection of <see cref="DynamicView"/> instances.  Often a
    /// <see cref="DynamicViewSet"/> instance will represent all the data that should be 
    /// replicated to a specific client.
    /// </summary>
    public class DynamicViewSet : IDisposable {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        protected readonly Action<DataEventTransaction> listener;
        protected readonly Func<DataEventTransaction, Task> asyncListener;

        protected readonly List<DynamicView> dynamicViews = new List<DynamicView>();
        protected readonly ConcurrentQueue<DataEventTransaction> incomingDataEventTransactions = new ConcurrentQueue<DataEventTransaction>();

        protected readonly CancellationTokenSource runCancellationTokenSource = new CancellationTokenSource();
        protected readonly AsyncMonitor monitor = new AsyncMonitor();

        protected readonly List<IDisposable> disposables = new List<IDisposable>();

        public DynamicViewSet(BaseDatabase database, Action<DataEventTransaction> listener) {
            this.Id = Guid.NewGuid().ToString();
            this.Database = database;
            this.listener = listener;
        }

        public DynamicViewSet(BaseDatabase database, Func<DataEventTransaction, Task> asyncListener) {
            this.Id = Guid.NewGuid().ToString();
            this.Database = database;
            this.asyncListener = asyncListener;
        }

        public string Id {
            get;
            protected set;
        }

        internal BaseDatabase Database {
            get;
            set;
        }

        /// <summary>
        /// Creates an instance of a DynamicView. Must call <see cref="StartAsync"/> to send 
        /// initial <see cref="DataEventTransaction"/> instance and listen for new <see cref="DataEventTransaction"/>instances.
        /// </summary>
        public DynamicView CreateDynamicView(string sql, dynamic values = null, string name = null, string[] keyFieldNames = null) {
            DynamicView dynamicQuery = new DynamicView(this, sql, values, name, keyFieldNames);
            this.dynamicViews.Add(dynamicQuery);
            return dynamicQuery;
        }

        protected bool isStarted = false;
        /// <summary>
        /// Send an initial <see cref="DataEventTransaction"/> to the registered listener and
        /// sends new <see cref="DataEventTransaction"/> instances when any data in the underlying
        /// <see cref="DynamicView"/> instances changes. Stops listening <see cref="Dispose"/> is called.
        /// </summary>
        /// <returns></returns>
        public async Task<DynamicViewSet> StartAsync() {
            logger.Debug("StartAsync");
            if (this.isStarted) throw new Exception("Dynamic Select Group is already started");
            if (this.runCancellationTokenSource.IsCancellationRequested) throw new Exception("Cannot restart a stopped DynamicViewSet");

            this.isStarted = true;

            DataEvent[] dataEvents = await this.RequeryDynamicViewsAsync(false);
            await this.SendToListenerAsync(new DataEventTransaction(DateTime.Now, dataEvents));

            this.Database.OnNewUncommittedTransaction(this.ProcessUncommittedDataEventTransactionAsync);
            this.Database.OnNewCommittedTransaction(this.ProcessCommittedDataEventTransactionAsync);

            Task backgroundTask = Task.Run(this.RunAsync);

            return this;
        }

        protected async Task ProcessUncommittedDataEventTransactionAsync(DataEventTransaction dataEventTransaction) {
            await this.StoreImpactedRecordsInDataEventTransaction(TransactionState.Uncommitted, dataEventTransaction);
        }

        protected async Task ProcessCommittedDataEventTransactionAsync(DataEventTransaction dataEventTransaction) {
            await this.StoreImpactedRecordsInDataEventTransaction(TransactionState.Committed, dataEventTransaction);
            this.incomingDataEventTransactions.Enqueue(dataEventTransaction);
            this.monitor.PulseAll();
        }

        protected async Task StoreImpactedRecordsInDataEventTransaction(TransactionState transactionState, DataEventTransaction dataEventTransaction) {
            foreach (var dynamicView in this.dynamicViews) {
                foreach (var dataEvent in dataEventTransaction.dataEvents) {
                    if (dataEvent is KeyValueDataEvent keyValueDataEvent && HasImpactedRecords(transactionState, keyValueDataEvent)) {
                        Dict[] impactedRecords = await dynamicView.GetImpactedRecordsAsync(keyValueDataEvent);
                        if (impactedRecords != null && impactedRecords.Length > 0) {
                            string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, transactionState);
                            dataEventTransaction.Store(storageKey, impactedRecords);
                        }
                    }
                }
            }
        }

        protected string GetImpactedRecordsStorageKey(DynamicView dynamicView, DataEvent dataEvent, TransactionState transactionState) {
            return $"{dynamicView.Id} {dataEvent.id} {transactionState}";
        }

        protected bool HasImpactedRecords(TransactionState transactionState, DataEvent dataEvent) {
            switch (transactionState) {
                case TransactionState.Uncommitted:
                    return dataEvent.dataEventType == DataEventType.Update || dataEvent.dataEventType == DataEventType.Delete;
                case TransactionState.Committed:
                    return dataEvent.dataEventType == DataEventType.Update || dataEvent.dataEventType == DataEventType.Insert;
            }
            return false;
        }

        /// <summary>
        /// Processes queued data change transactions (runs on a background thread)
        /// </summary>
        /// <returns></returns>
        protected async Task RunAsync() {
            while (!this.runCancellationTokenSource.IsCancellationRequested) {
                try {
                    if (this.incomingDataEventTransactions.TryDequeue(out DataEventTransaction dataEventTransaction)) {
                        logger.Debug($"RunAsync():dataEventTransaction={dataEventTransaction}");
                        List<DataEvent> newDataEvents = new List<DataEvent>();
                        foreach (var dataEvent in dataEventTransaction.dataEvents) {
                            logger.Debug($"RunAsync():dataEventTransaction.dataEvents.Length={dataEventTransaction.dataEvents.Length}");
                            foreach (var dynamicView in this.dynamicViews) {
                                // Don't send data events if DynamicView has dirty params because
                                // the DynamicView will be requeried anyways
                                if (!dynamicView.HasDirtyParams) {
                                    // Fetch the preCommitImpactedRecords
                                    Dict[] preCommitImpactedRecords = null;
                                    if (HasImpactedRecords(TransactionState.Uncommitted, dataEvent)) {
                                        string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, TransactionState.Uncommitted);
                                        preCommitImpactedRecords = (Dict[])dataEventTransaction.Fetch(storageKey);
                                    }

                                    // Fetch the postCommitImpactedRecords
                                    Dict[] postCommitImpactedRecords = null;
                                    if (HasImpactedRecords(TransactionState.Committed, dataEvent)) {
                                        string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, TransactionState.Committed);
                                        postCommitImpactedRecords = (Dict[])dataEventTransaction.Fetch(storageKey);
                                    }

                                    // Determine the changes from each data event on each dynamic select
                                    RecordDataEvent[] newChangeDataEvents = dynamicView.ProcessDataChange(dataEvent, preCommitImpactedRecords, postCommitImpactedRecords);
                                    if (newChangeDataEvents != null) {
                                        dynamicView.UpdateChildDynamicParams(newChangeDataEvents);
                                        newDataEvents.AddRange(newChangeDataEvents);
                                    }
                                }
                            }
                        }

                        DataEvent[] initialDataEvents = await this.RequeryDynamicViewsAsync(true);
                        newDataEvents.AddRange(initialDataEvents);

                        if (newDataEvents.Count > 0) {
                            await this.SendToListenerAsync(new DataEventTransaction(dataEventTransaction.dateTime, newDataEvents.ToArray()));
                        }
                    }
                    else {
                        using (var monitorWait = await this.monitor.EnterAsync(this.runCancellationTokenSource.Token)) {
                            await this.monitor.WaitAsync();
                        }
                    }
                }
                catch (Exception e) {
                    logger.Debug(e);
                    await Task.Delay(100);
                }
            }
        }

        protected async Task SendToListenerAsync(DataEventTransaction dataEventTransaction) {
            if (logger.IsTraceEnabled) logger.Debug($"SendToListenerAsync():dataEventTransaction.dataEvents={dataEventTransaction.dataEvents}");
            else if (logger.IsDebugEnabled) logger.Debug($"SendToListenerAsync():dataEventTransaction.dataEvents.Length={dataEventTransaction.dataEvents.Length}");

            if (this.listener != null) {
                this.listener(dataEventTransaction);
            }
            if (this.asyncListener != null) {
                await asyncListener(dataEventTransaction);
            }
        }

        /// <summary>
        /// Return the initial query results if any of the query parameters have changed or if passed force=true
        /// </summary>
        /// <param name="onlyIfDirtyParams"></param>
        /// <returns></returns>
        protected async Task<DataEvent[]> RequeryDynamicViewsAsync(bool onlyIfDirtyParams) {
            logger.Debug($"RequeryDynamicViewsIfDirtyAsync():onlyIfDirtyParams={onlyIfDirtyParams}");
            List<DataEvent> dataEvents = new List<DataEvent>();
            foreach (var dynamicView in this.dynamicViews) {
                if (!onlyIfDirtyParams || dynamicView.HasDirtyParams) {
                    DataEvent[] initialDataEvents = await dynamicView.GetInitialDataEventsAsync();
                    dataEvents.AddRange(initialDataEvents);
                    dynamicView.ResetDirtyParams();
                    dynamicView.UpdateChildDynamicParams(initialDataEvents);
                }
            }
            return dataEvents.ToArray();
        }

        public void Dispose() {
            logger.Debug("Dispose()");
            foreach (var disposable in this.disposables) {
                disposable.Dispose();
            }
            this.runCancellationTokenSource.Cancel();
        }

    }
}
