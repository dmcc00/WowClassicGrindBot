﻿using Microsoft.Extensions.Logging;
using System;
using System.Numerics;
using System.Threading;
using SharedLib.Extensions;
using static System.MathF;

#pragma warning disable 162

namespace Core;

public sealed partial class PlayerDirection : IDisposable
{
    private const bool debug = false;

    private const int DefaultIgnoreDistance = 10;

    private readonly ILogger<PlayerDirection> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly CancellationTokenSource _cts;

    public PlayerDirection(ILogger<PlayerDirection> logger,
        ConfigurableInput input, PlayerReader playerReader)
    {
        this.logger = logger;
        this.input = input;
        this.playerReader = playerReader;
        _cts = new();
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    public void SetDirection(float targetDir, Vector3 map)
    {
        SetDirection(targetDir, map, DefaultIgnoreDistance, _cts.Token);
    }

    public void SetDirection(float targetDir, Vector3 world, float ignoreDistance, CancellationToken ct)
    {
        float distance = playerReader.WorldPos.WorldDistanceXYTo(world);
        if (distance < ignoreDistance)
        {
            if (debug)
                LogDebugClose(logger, distance, ignoreDistance);

            return;
        }

        if (debug)
            LogDebugSetDirection(logger, playerReader.Direction, targetDir, distance);

        input.PressFixed(GetDirectionKeyToPress(targetDir),
            TurnDuration(targetDir), ct);
    }

    private float TurnAmount(float targetDir)
    {
        float result = (Tau + targetDir - playerReader.Direction) % Tau;
        return result > PI
            ? Tau - result
            : result;
    }

    private int TurnDuration(float targetDir)
    {
        return (int)(TurnAmount(targetDir) * 1000 / PI);
    }

    private ConsoleKey GetDirectionKeyToPress(float desiredDirection)
    {
        return (Tau + desiredDirection - playerReader.Direction) % Tau < PI
            ? input.TurnLeftKey
            : input.TurnRightKey;
    }

    #region Logging

    [LoggerMessage(
        EventId = 0030,
        Level = LogLevel.Debug,
        Message = "SetDirection: Too close, ignored direction change. {distance} < {ignoreDistance}")]
    static partial void LogDebugClose(ILogger logger, float distance, float ignoreDistance);

    [LoggerMessage(
        EventId = 0031,
        Level = LogLevel.Debug,
        Message = "SetDirection: {direction:0.000} -> {desiredDirection:0.000} - {distance:0.000}")]
    static partial void LogDebugSetDirection(ILogger logger, float direction, float desiredDirection, float distance);

    #endregion
}