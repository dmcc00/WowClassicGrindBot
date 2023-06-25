﻿using System;
using System.Threading;

using Microsoft.Extensions.Logging;

#pragma warning disable 162

namespace Core.Goals;

public sealed class CastingHandlerInterruptWatchdog : IDisposable
{
    private const bool Log = false;

    private readonly ILogger<CastingHandlerInterruptWatchdog> logger;
    private readonly Wait wait;

    private readonly Thread thread;
    private readonly CancellationTokenSource threadCts;
    private readonly ManualResetEventSlim resetEvent;

    private bool? initial;
    private Func<bool>? interrupt;
    private CancellationTokenSource? cts;

    public CastingHandlerInterruptWatchdog(
        ILogger<CastingHandlerInterruptWatchdog> logger, Wait wait)
    {
        this.logger = logger;
        this.wait = wait;

        threadCts = new();
        resetEvent = new(false);

        thread = new(Watchdog);
        thread.Start();
    }

    public void Dispose()
    {
        interrupt = null;

        threadCts.Cancel();
        resetEvent.Set();
    }

    private void Watchdog()
    {
        while (!threadCts.IsCancellationRequested)
        {
            while (interrupt != null &&
                cts != null && !cts.IsCancellationRequested)
            {
                if (initial != interrupt?.Invoke())
                {
                    if (Log)
                        logger.LogWarning("Interrupted!");

                    initial = null;
                    interrupt = null;

                    cts?.Cancel();
                    cts = null;

                    break;
                }

                wait.Update();
            }

            if (Log)
                logger.LogWarning("Waiting...");

            resetEvent.Reset();
            resetEvent.Wait();
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Thread stopped!");
    }

    public void Set(Func<bool> interrupt, CancellationTokenSource cts)
    {
        if (Log)
            logger.LogWarning("Set interrupt");

        this.initial = interrupt();
        this.interrupt = interrupt;
        this.cts = cts;

        resetEvent.Set();
    }

    public void Reset()
    {
        initial = null;
        interrupt = null;

        if (cts != null && !cts.IsCancellationRequested)
            cts?.Cancel();
    }
}
