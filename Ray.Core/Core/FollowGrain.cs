﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Ray.Core.Configuration;
using Ray.Core.Event;
using Ray.Core.Exceptions;
using Ray.Core.Logging;
using Ray.Core.Serialization;
using Ray.Core.State;
using Ray.Core.Storage;

namespace Ray.Core
{
    public abstract class FollowGrain<MainGrain, PrimaryKey> : Grain, IFollow
    {
        public FollowGrain(ILogger logger)
        {
            Logger = logger;
            GrainType = GetType();
        }
        public abstract PrimaryKey GrainId { get; }
        protected Type BytesWrapperType { get; set; }
        protected CoreOptions<MainGrain> ConfigOptions { get; private set; }
        protected ILogger Logger { get; private set; }
        protected IJsonSerializer JsonSerializer { get; private set; }
        protected ISerializer Serializer { get; private set; }
        protected IStorageFactory StorageFactory { get; private set; }
        /// <summary>
        /// Memory state, restored by snapshot + Event play or replay
        /// </summary>
        protected FollowSnapshot<PrimaryKey> Snapshot { get; set; }
        /// <summary>
        /// 是否需要保存快照
        /// </summary>
        protected virtual bool SaveSnapshot => true;
        /// <summary>
        /// 是否全量激活，true代表启动时会执行大于快照版本的所有事件,false代表更快的启动，后续有事件进入的时候再处理大于快照版本的事件
        /// </summary>
        protected virtual bool FullyActive => false;
        /// <summary>
        /// 快照的事件版本号
        /// </summary>
        protected long SnapshotEventVersion { get; set; }
        /// <summary>
        /// 是否开启事件并发处理
        /// </summary>
        protected virtual bool EventConcurrentProcessing => false;
        /// <summary>
        /// Grain的Type
        /// </summary>
        protected Type GrainType { get; }
        /// <summary>
        /// 事件存储器
        /// </summary>
        protected IEventStorage<PrimaryKey> EventStorage { get; private set; }
        /// <summary>
        /// 状态存储器
        /// </summary>
        protected IFollowSnapshotStorage<PrimaryKey> FollowSnapshotStorage { get; private set; }
        #region 初始化数据
        /// <summary>
        /// 依赖注入统一方法
        /// </summary>
        protected async virtual ValueTask DependencyInjection()
        {
            BytesWrapperType = ServiceProvider.GetService<IBytesWrapper>().GetType();
            ConfigOptions = ServiceProvider.GetService<IOptions<CoreOptions<MainGrain>>>().Value;
            StorageFactory = ServiceProvider.GetService<IStorageFactoryContainer>().CreateFactory(GrainType);
            Serializer = ServiceProvider.GetService<ISerializer>();
            JsonSerializer = ServiceProvider.GetService<IJsonSerializer>();
            //创建事件存储器
            var eventStorageTask = StorageFactory.CreateEventStorage<PrimaryKey>(this, GrainId);
            if (!eventStorageTask.IsCompleted)
                await eventStorageTask;
            EventStorage = eventStorageTask.Result;
            //创建状态存储器
            var stateStorageTask = StorageFactory.CreateFollowSnapshotStorage<PrimaryKey>(this, GrainId);
            if (!stateStorageTask.IsCompleted)
                await stateStorageTask;
            FollowSnapshotStorage = stateStorageTask.Result;
        }
        public override async Task OnActivateAsync()
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.GrainActivateId, "Start activation followgrain with id = {0}", GrainId.ToString());
            var dITask = DependencyInjection();
            if (!dITask.IsCompleted)
                await dITask;
            try
            {
                await ReadSnapshotAsync();
                if (FullyActive)
                {
                    await FullActive();
                }
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.GrainActivateId, "Followgrain activation completed with id = {0}", GrainId.ToString());
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.FollowGrainActivateId, ex, "Followgrain activation failed with Id = {0}", GrainId.ToString());
                throw;
            }
        }
        private async Task FullActive()
        {
            while (true)
            {
                var eventList = await EventStorage.GetList(GrainId, 0, Snapshot.Version, Snapshot.Version + ConfigOptions.NumberOfEventsPerRead);
                if (EventConcurrentProcessing)
                {
                    await Task.WhenAll(eventList.Select(@event =>
                    {
                        var task = OnEventDelivered(@event);
                        if (!task.IsCompleted)
                            return task.AsTask();
                        else
                            return Task.CompletedTask;
                    }));
                    var lastEvt = eventList.Last();
                    Snapshot.UnsafeUpdateVersion(lastEvt.GetBase().Version);
                }
                else
                {
                    foreach (var @event in eventList)
                    {
                        Snapshot.IncrementDoingVersion(GrainType);//标记将要处理的Version
                        var task = OnEventDelivered(@event);
                        if (!task.IsCompleted)
                            await task;
                        Snapshot.UpdateVersion(@event, GrainType);//更新处理完成的Version
                    }
                }
                var saveTask = SaveSnapshotAsync();
                if (!saveTask.IsCompleted)
                    await saveTask;
                if (eventList.Count < ConfigOptions.NumberOfEventsPerRead) break;
            };
        }
        public override Task OnDeactivateAsync()
        {
            var needSaveSnap = Snapshot.Version - SnapshotEventVersion >= 1;
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation(LogEventIds.FollowGrainDeactivateId, "Followgrain start deactivation with id = {0} ,{1}", GrainId.ToString(), needSaveSnap ? "updated snapshot" : "no update snapshot");
            if (needSaveSnap)
                return SaveSnapshotAsync(true).AsTask();
            else
                return Task.CompletedTask;
        }
        /// <summary>
        /// true:当前状态无快照,false:当前状态已经存在快照
        /// </summary>
        protected bool NoSnapshot { get; private set; }
        protected virtual async Task ReadSnapshotAsync()
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.GrainSnapshot, "Start read snapshot  with Id = {0} ,state version = {1}", GrainId.ToString(), Snapshot.Version);
            try
            {
                Snapshot = await FollowSnapshotStorage.Get(GrainId);
                if (Snapshot == null)
                {
                    NoSnapshot = true;
                    var createTask = CreateState();
                    if (!createTask.IsCompleted)
                        await createTask;
                }
                SnapshotEventVersion = Snapshot.Version;
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.GrainSnapshot, "The snapshot of id = {0} read completed, state version = {1}", GrainId.ToString(), Snapshot.Version);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.GrainSnapshot, ex, "The snapshot of id = {0} read failed", GrainId.ToString());
                throw;
            }
        }
        /// <summary>
        /// 初始化状态，必须实现
        /// </summary>
        /// <returns></returns>
        protected virtual ValueTask CreateState()
        {
            Snapshot = new FollowSnapshot<PrimaryKey>
            {
                StateId = GrainId
            };
            return Consts.ValueTaskDone;
        }
        #endregion
        public Task Tell(byte[] bytes)
        {
            using (var wms = new MemoryStream(bytes))
            {
                var data = Serializer.Deserialize(BytesWrapperType, wms);
                if (data is IBytesWrapper message)
                {
                    using (var ems = new MemoryStream(message.Bytes))
                    {
                        if (Serializer.Deserialize(TypeContainer.GetType(message.TypeName), ems) is IEvent<PrimaryKey> @event)
                        {
                            var tellTask = Tell(@event);
                            if (!tellTask.IsCompleted)
                                return tellTask.AsTask();
                        }
                        else
                        {
                            if (Logger.IsEnabled(LogLevel.Information))
                                Logger.LogInformation(LogEventIds.FollowEventProcessing, "Receive non-event messages, grain Id = {0} ,message type = {1}", GrainId.ToString(), message.TypeName);
                        }
                    }
                }
            }
            return Task.CompletedTask;
        }
        public Task<long> CurrentVersion()
        {
            return Task.FromResult(Snapshot.Version);
        }
        protected async ValueTask Tell(IEvent<PrimaryKey> @event)
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.FollowEventProcessing, "Start event handling, grain Id = {0} and state version = {1},event type = {2} ,event = {3}", GrainId.ToString(), Snapshot.Version, @event.GetType().FullName, JsonSerializer.Serialize(@event));
            try
            {
                var eventBase = @event.GetBase();
                if (eventBase.Version == Snapshot.Version + 1)
                {
                    var onEventDeliveredTask = OnEventDelivered(@event);
                    if (!onEventDeliveredTask.IsCompleted)
                        await onEventDeliveredTask;
                    Snapshot.FullUpdateVersion(@event, GrainType);//更新处理完成的Version
                }
                else if (eventBase.Version > Snapshot.Version)
                {
                    var eventList = await EventStorage.GetList(GrainId, 0, Snapshot.Version, eventBase.Version);
                    foreach (var item in eventList)
                    {
                        var onEventDeliveredTask = OnEventDelivered(item);
                        if (!onEventDeliveredTask.IsCompleted)
                            await onEventDeliveredTask;
                        Snapshot.FullUpdateVersion(item, GrainType);//更新处理完成的Version
                    }
                }
                if (eventBase.Version == Snapshot.Version + 1)
                {
                    var onEventDeliveredTask = OnEventDelivered(@event);
                    if (!onEventDeliveredTask.IsCompleted)
                        await onEventDeliveredTask;
                    Snapshot.FullUpdateVersion(@event, GrainType);//更新处理完成的Version
                }
                if (eventBase.Version > Snapshot.Version)
                {
                    throw new EventVersionNotMatchStateException(GrainId.ToString(), GrainType, eventBase.Version, Snapshot.Version);
                }
                await SaveSnapshotAsync();
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.FollowEventProcessing, "Event Handling Completion, grain Id ={0} and state version = {1},event type = {2}", GrainId.ToString(), Snapshot.Version, @event.GetType().FullName);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.FollowEventProcessing, ex, "FollowGrain Event handling failed with Id = {0},event = {1}", GrainId.ToString(), JsonSerializer.Serialize(@event));
                throw;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnEventDelivered(IEvent<PrimaryKey> @event) => Consts.ValueTaskDone;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSaveSnapshot() => Consts.ValueTaskDone;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSavedSnapshot() => new ValueTask();
        protected virtual async ValueTask SaveSnapshotAsync(bool force = false)
        {
            if (SaveSnapshot)
            {
                if (force || (Snapshot.Version - SnapshotEventVersion >= ConfigOptions.FollowSnapshotVersionInterval))
                {
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace(LogEventIds.FollowGrainSaveSnapshot, "Start saving state snapshots with Id = {0} ,state version = {1}", GrainId.ToString(), Snapshot.Version);
                    try
                    {
                        var onSaveSnapshotTask = OnSaveSnapshot();//自定义保存项
                        if (!onSaveSnapshotTask.IsCompleted)
                            await onSaveSnapshotTask;
                        if (NoSnapshot)
                        {
                            await FollowSnapshotStorage.Insert(Snapshot);
                            NoSnapshot = false;
                        }
                        else
                        {
                            await FollowSnapshotStorage.Update(Snapshot);
                        }
                        SnapshotEventVersion = Snapshot.Version;
                        var onSavedSnapshotTask = OnSavedSnapshot();
                        if (!onSavedSnapshotTask.IsCompleted)
                            await onSavedSnapshotTask;
                        if (Logger.IsEnabled(LogLevel.Trace))
                            Logger.LogTrace(LogEventIds.FollowGrainSaveSnapshot, "State snapshot saved successfully with Id {0} ,state version = {1}", GrainId.ToString(), Snapshot.Version);
                    }
                    catch (Exception ex)
                    {
                        if (Logger.IsEnabled(LogLevel.Error))
                            Logger.LogError(LogEventIds.FollowGrainSaveSnapshot, ex, "State snapshot save failed with Id = {0}", GrainId.ToString());
                        throw;
                    }
                }
            }
        }
    }
}
