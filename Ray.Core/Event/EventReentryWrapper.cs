﻿using System;
using System.Threading.Tasks;

namespace Ray.Core.Event
{
    public class EventReentryWrapper<K, S>
    {
        public EventReentryWrapper(
            Func<S, Func<IEventBase<K>, EventUID, Task>, Task> handler,
            Func<bool, ValueTask> completedHandler,
            Action<Exception> exceptionHandler)
        {
            Handler = handler;
            ExceptionHandler = exceptionHandler;
            CompletedHandler = completedHandler;
        }
        public bool Executed { get; set; }
        public Func<S, Func<IEventBase<K>, EventUID, Task>, Task> Handler { get; }
        public Func<bool, ValueTask> CompletedHandler { get; }
        public Action<Exception> ExceptionHandler { get; }
    }
}