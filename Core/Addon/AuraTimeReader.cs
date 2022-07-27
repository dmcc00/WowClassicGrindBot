﻿using System;
using System.Collections.Generic;

namespace Core
{
    public class AuraTimeReader
    {
        public readonly struct Data
        {
            public int DurationSec { get; }
            public DateTime StartTime { get; }

            public Data(int duration, DateTime startTime)
            {
                DurationSec = duration;
                StartTime = startTime;
            }
        }

        private readonly int cTextureId;
        private readonly int cDurationSec;

        private readonly Dictionary<int, Data> data = new();

        public AuraTimeReader(int cTextureId, int cDurationSec)
        {
            this.cTextureId = cTextureId;
            this.cDurationSec = cDurationSec;
            Reset();
        }

        public void Read(AddonDataProvider reader)
        {
            int textureId = reader.GetInt(cTextureId);
            if (textureId == 0) return;

            int durationSec = reader.GetInt(cDurationSec);
            data[textureId] = new(durationSec, DateTime.UtcNow);
        }

        public void Reset()
        {
            data.Clear();
        }

        public int GetRemainingTimeMs(int id)
        {
            return data.TryGetValue(id, out Data d) ? Math.Max(0, (int)(d.StartTime.AddSeconds(d.DurationSec) - DateTime.UtcNow).TotalMilliseconds) : 0;
        }

        public int GetTotalTimeMs(PlayerReader playerReader, KeyAction keyAction)
        {
            int index = Stance.ToSlot(keyAction, playerReader) - 1;
            return data[index].DurationSec * 1000;
        }

    }
}