﻿using Core.GOAP;
using SharedLib.NpcFinder;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Goals
{
    public class AdhocNPCGoal : GoapGoal, IRouteProvider
    {
        private double RADIAN = Math.PI * 2;

        private readonly ILogger logger;
        private readonly ConfigurableInput input;

        private readonly PlayerReader playerReader;
        private readonly IPlayerDirection playerDirection;
        private readonly StopMoving stopMoving;
        private readonly StuckDetector stuckDetector;
        private readonly ClassConfiguration classConfiguration;
        private readonly NpcNameTargeting npcNameTargeting;
        private readonly IBlacklist blacklist;
        private readonly IPPather pather;
        private readonly MountHandler mountHandler;

        private readonly Wait wait;
        private readonly ExecGameCommand execGameCommand;
        private readonly GossipReader gossipReader;

        private Stack<WowPoint> routeToWaypoint = new Stack<WowPoint>();

        public List<WowPoint> PathingRoute()
        {
            return routeToWaypoint.ToList();
        }

        public WowPoint? NextPoint()
        {
            return routeToWaypoint.Count == 0 ? null : routeToWaypoint.Peek();
        }

        private double lastDistance = 999;
        public DateTime LastActive { get; set; } = DateTime.Now.AddDays(-1);
        private bool shouldMount = true;
        
        private readonly KeyAction key;

        public AdhocNPCGoal(ILogger logger, ConfigurableInput input, PlayerReader playerReader, IPlayerDirection playerDirection, StopMoving stopMoving, NpcNameTargeting npcNameTargeting, StuckDetector stuckDetector, ClassConfiguration classConfiguration, IPPather pather, KeyAction key, IBlacklist blacklist, MountHandler mountHandler, Wait wait, ExecGameCommand exec, GossipReader gossipReader)
        {
            this.logger = logger;
            this.input = input;
            this.playerReader = playerReader;
            this.playerDirection = playerDirection;
            this.stopMoving = stopMoving;
            this.npcNameTargeting = npcNameTargeting;
            
            this.stuckDetector = stuckDetector;
            this.classConfiguration = classConfiguration;
            this.pather = pather;
            this.key = key;
            this.blacklist = blacklist;
            this.mountHandler = mountHandler;

            this.wait = wait;
            this.execGameCommand = exec;
            this.gossipReader = gossipReader;

            if (key.InCombat == "false")
            {
                AddPrecondition(GoapKey.incombat, false);
            }
            else if (key.InCombat == "true")
            {
                AddPrecondition(GoapKey.incombat, true);
            }

            this.Keys.Add(key);
        }

        public override float CostOfPerformingAction { get => key.Cost; }

        public override bool CheckIfActionCanRun()
        {
            return this.key.CanRun();
        }

        public override void OnActionEvent(object sender, ActionEventArgs e)
        {
            if (sender != this)
            {
                shouldMount = true;
                this.routeToWaypoint.Clear();
            }
        }

        public override async Task PerformAction()
        {
            SendActionEvent(new ActionEventArgs(GoapKey.fighting, false));

            await Task.Delay(200);

            if (this.playerReader.PlayerBitValues.PlayerInCombat && this.classConfiguration.Mode != Mode.AttendedGather) { return; }

            if ((DateTime.Now - LastActive).TotalSeconds > 10 || routeToWaypoint.Count == 0)
            {
                await FillRouteToDestination();
            }
            else
            {
                input.SetKeyState(ConsoleKey.UpArrow, true, false, "NPC Goal 1");
            }

            var location = new WowPoint(playerReader.XCoord, playerReader.YCoord, playerReader.ZCoord);
            var distance = WowPoint.DistanceTo(location, routeToWaypoint.Peek());
            var heading = DirectionCalculator.CalculateHeading(location, routeToWaypoint.Peek());

            await AdjustHeading(heading);

            if (lastDistance < distance)
            {
                await playerDirection.SetDirection(heading, routeToWaypoint.Peek(), "Further away");
            }
            else if (!this.stuckDetector.IsGettingCloser())
            {
                // stuck so jump
                input.SetKeyState(ConsoleKey.UpArrow, true, false, "NPC Goal 2");
                await Task.Delay(100);
                if (HasBeenActiveRecently())
                {
                    await this.stuckDetector.Unstick();
                }
                else
                {
                    await Task.Delay(1000);
                    logger.LogInformation("Resuming movement");
                }
            }

            lastDistance = distance;

            if (distance < PointReachedDistance())
            {
                logger.LogInformation($"Move to next point");
                if (routeToWaypoint.Any())
                {
                    playerReader.ZCoord = routeToWaypoint.Peek().Z;
                    logger.LogInformation($"{GetType().Name}: PlayerLocation.Z = {playerReader.PlayerLocation.Z}");
                }

                ReduceRoute();

                lastDistance = 999;
                if (routeToWaypoint.Count == 0)
                {
                    await this.stopMoving.Stop();
                    distance = WowPoint.DistanceTo(location, this.StartOfPathToNPC());
                    if (distance > 50)
                    {
                        await FillRouteToDestination();
                    }

                    if (routeToWaypoint.Count == 0)
                    {
                        await MoveCloserToPoint(400, StartOfPathToNPC());
                        await MoveCloserToPoint(100, StartOfPathToNPC());

                        // we have reached the start of the path to the npc
                        await FollowPath(this.key.Path);

                        await this.stopMoving.Stop();
                        await input.TapClearTarget();
                        await wait.Update(1);

                        npcNameTargeting.ChangeNpcType(NpcNames.Friendly | NpcNames.Neutral);
                        await npcNameTargeting.WaitForNUpdate(2);
                        var foundVendor = await npcNameTargeting.FindByCursorType(Cursor.CursorClassification.Vendor, Cursor.CursorClassification.Repair);

                        await InteractWithTarget();
                        await input.TapClearTarget();

                        // walk back to the start of the path to the npc
                        var pathFrom = this.key.Path.ToList();
                        pathFrom.Reverse();
                        await FollowPath(pathFrom);
                    }
                }

                if (routeToWaypoint.Count == 0)
                {
                    return;
                }

                this.stuckDetector.SetTargetLocation(this.routeToWaypoint.Peek());

                heading = DirectionCalculator.CalculateHeading(location, routeToWaypoint.Peek());
                await playerDirection.SetDirection(heading, routeToWaypoint.Peek(), "Move to next point");
            }

            // should mount
            await MountIfRequired();

            LastActive = DateTime.Now;
        }

        private async Task FollowPath(List<WowPoint> path)
        {
            // show route on map
            this.routeToWaypoint.Clear();
            var rpath = path.ToList();
            rpath.Reverse();
            rpath.ForEach(p => this.routeToWaypoint.Push(p));

            if (this.playerReader.PlayerBitValues.IsMounted)
            {
                await input.TapDismount();
            }

            foreach (var point in path)
            {
                await MoveCloserToPoint(400, point);
            }
        }

        private async Task MoveCloserToPoint(int pressDuration, WowPoint target)
        {
            logger.LogInformation($"Moving to spot = {target}");

            var distance = WowPoint.DistanceTo(playerReader.PlayerLocation, target);
            var lastDistance = distance;
            while (distance <= lastDistance && distance > 5)
            {
                if (this.playerReader.HealthPercent == 0) { return; }

                logger.LogInformation($"Distance to spot = {distance}");
                lastDistance = distance;
                var heading = DirectionCalculator.CalculateHeading(playerReader.PlayerLocation, target);
                await playerDirection.SetDirection(heading, this.StartOfPathToNPC(), "Correcting direction", 0);

                await this.input.KeyPress(ConsoleKey.UpArrow, pressDuration);
                await this.stopMoving.Stop();
                distance = WowPoint.DistanceTo(playerReader.PlayerLocation, target);
            }
        }

        private async Task MountIfRequired()
        {
            if (shouldMount && !playerReader.PlayerBitValues.IsMounted && !playerReader.PlayerBitValues.PlayerInCombat)
            {
                shouldMount = false;

                await mountHandler.MountUp();

                input.SetKeyState(ConsoleKey.UpArrow, true, false, "Move forward");
            }
        }

        private void ReduceRoute()
        {
            if (routeToWaypoint.Any())
            {
                var location = new WowPoint(playerReader.XCoord, playerReader.YCoord, playerReader.ZCoord);
                var distance = WowPoint.DistanceTo(location, routeToWaypoint.Peek());
                while (distance < PointReachedDistance() && routeToWaypoint.Any())
                {
                    routeToWaypoint.Pop();
                    if (routeToWaypoint.Any())
                    {
                        distance = WowPoint.DistanceTo(location, routeToWaypoint.Peek());
                    }
                }
            }
        }

        private async Task FillRouteToDestination()
        {
            this.routeToWaypoint.Clear();
            WowPoint target = StartOfPathToNPC();
            var location = new WowPoint(playerReader.XCoord, playerReader.YCoord, playerReader.ZCoord);
            var heading = DirectionCalculator.CalculateHeading(location, target);
            await playerDirection.SetDirection(heading, target, "Set Location target").ConfigureAwait(false);

            // create route to vendo
            await this.stopMoving.Stop();
            var path = await this.pather.FindRouteTo(this.playerReader, target);
            path.Reverse();
            path.ForEach(p => this.routeToWaypoint.Push(p));

            if (routeToWaypoint.Any())
            {
                playerReader.ZCoord = routeToWaypoint.Peek().Z;
                logger.LogInformation($"{GetType().Name}: PlayerLocation.Z = {playerReader.PlayerLocation.Z}");
            }

            this.ReduceRoute();
            if (this.routeToWaypoint.Count == 0)
            {
                this.routeToWaypoint.Push(target);
            }

            this.stuckDetector.SetTargetLocation(this.routeToWaypoint.Peek());
        }

        private WowPoint StartOfPathToNPC()
        {
            if (!this.key.Path.Any())
            {
                this.logger.LogError("Path to target is not defined");
                throw new Exception("Path to target is not defined");
            }

            return this.key.Path[0];
        }

        private async Task AdjustHeading(double heading)
        {
            var diff1 = Math.Abs(RADIAN + heading - playerReader.Direction) % RADIAN;
            var diff2 = Math.Abs(heading - playerReader.Direction - RADIAN) % RADIAN;

            var wanderAngle = 0.3;

            if (this.classConfiguration.Mode != Mode.AttendedGather)
            {
                wanderAngle = 0.05;
            }

            if (Math.Min(diff1, diff2) > wanderAngle)
            {
                logger.LogInformation("Correct direction");
                await playerDirection.SetDirection(heading, routeToWaypoint.Peek(), "Correcting direction");
            }
            else
            {
                logger.LogInformation($"Direction ok heading: {heading}, player direction {playerReader.Direction}");
            }
        }

        private int PointReachedDistance()
        {
            if (this.playerReader.PlayerClass == PlayerClassEnum.Druid && this.playerReader.Form == Form.Druid_Travel)
            {
                return 50;
            }

            return (this.playerReader.PlayerBitValues.IsMounted ? 50 : 20);
        }

        private bool HasBeenActiveRecently()
        {
            return (DateTime.Now - LastActive).TotalSeconds < 2;
        }

        public override string Name => this.Keys.Count == 0 ? base.Name : this.Keys[0].Name;


        private async Task InteractWithTarget()
        {
            if (playerReader.HealthPercent == 0) 
            {
                logger.LogInformation("Target is dead!");
                return;
            }

            logger.LogInformation("Interacting with NPC");

            for (int i = 0; i < 5; i++)
            {
                await input.TapInteractKey("Make sure to the gossip window is open!");

                DateTime start = DateTime.Now;
                while (!gossipReader.MerchantWindowOpened && (DateTime.Now - start).TotalMilliseconds < 1)
                {
                    await wait.Update(1);

                    if (gossipReader.Count != 0)
                    {
                        logger.LogInformation($"There are gossip options! {gossipReader.Count}");

                        if (gossipReader.Gossips.TryGetValue(Gossip.Vendor, out int orderNum))
                        {
                            logger.LogInformation($"Pick {Gossip.Vendor} -> {orderNum}");
                            await execGameCommand.Run($"/run SelectGossipOption({orderNum})--");
                            await wait.Update(2);
                        }
                    }
                }

                // Macro runs: targets NPC and does action such as sell
                await this.input.KeyPress(key.ConsoleKey, 100);

                // Interact with NPC
                if (!string.IsNullOrEmpty(this.playerReader.Target))
                {
                    // black list it so we don't get stuck trying to kill it
                    this.blacklist.Add(this.playerReader.Target);

                    await input.TapInteractKey($"InteractWithTarget {i}");
                }
                else
                {
                    logger.LogError($"Error: No target has been selected. Key {key.ConsoleKey} should be /tar an NPC.");
                    break;
                }

                System.Threading.Thread.Sleep(1000);
                await this.input.KeyPress(ConsoleKey.Escape, 50);
            }

            if(CheckIfActionCanRun())
            {
                logger.LogError("We failed to do anything at the NPC! Aborting.");
            }
        }
    }
}