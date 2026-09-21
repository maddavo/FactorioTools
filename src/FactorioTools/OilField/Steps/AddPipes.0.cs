using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Knapcode.FactorioTools.Data;
using static Knapcode.FactorioTools.OilField.Helpers;

namespace Knapcode.FactorioTools.OilField;

public static class AddPipes
{
    public static (List<OilFieldPlan> SelectedPlans, List<OilFieldPlan> AlternatePlans, List<OilFieldPlan> UnusedPlans)
        Execute(Context context, bool eliminateStrandedTerminals)
    {
        if (eliminateStrandedTerminals)
        {
            EliminateStrandedTerminals(context);
        }

        List<OilFieldPlan> selectedPlans;
        List<OilFieldPlan> alternatePlans;
        List<OilFieldPlan> unusedPlans;
        Solution bestSolution;
        BeaconSolution? bestBeacons;

        var result = GetBestSolution(context);
        if (result.Exception is NoPathBetweenTerminalsException && !eliminateStrandedTerminals)
        {
            EliminateStrandedTerminals(context);
            result = GetBestSolution(context);
            if (result.Exception is not null)
            {
                throw result.Exception;
            }
        }

        (selectedPlans, alternatePlans, unusedPlans, bestSolution, bestBeacons) = result.Data!;

        context.CenterToTerminals = bestSolution.CenterToTerminals;
        context.LocationToTerminals = bestSolution.LocationToTerminals;

        AddPipeEntities.Execute(context, bestSolution.Pipes, bestSolution.UndergroundPipes);

        if (bestBeacons is not null)
        {
            // Visualizer.Show(context.Grid, bestSolution.Beacons.Select(c => (DelaunatorSharp.IPoint)new DelaunatorSharp.Point(c.X, c.Y)), Array.Empty<DelaunatorSharp.IEdge>());
            AddBeaconsToGrid(context.Grid, context.Options, bestBeacons.Beacons);
        }

        return (selectedPlans, alternatePlans, unusedPlans);
    }

    private record SolutionInfo(List<OilFieldPlan> SelectedPlans, List<OilFieldPlan> AltnernatePlans, List<OilFieldPlan> UnusedPlans, Solution BestSolution, BeaconSolution? BestBeacons);

    private static Result<SolutionInfo> GetBestSolution(Context context)
    {
        var result = GetAllPlans(context);
        if (result.Exception is not null)
        {
            return Result.NewException<SolutionInfo>(result.Exception);
        }

        var sortedPlans = result.Data!;
        sortedPlans.Sort((a, b) =>
        {
            // more effects = better
            var c = b.Plan.BeaconEffectCount.CompareTo(a.Plan.BeaconEffectCount);
            if (c != 0)
            {
                return c;
            }

            // fewer beacons = better (less power)
            c = a.Plan.BeaconCount.CompareTo(b.Plan.BeaconCount);
            if (c != 0)
            {
                return c;
            }

            // fewer pipes = better
            c = a.Plan.PipeCount.CompareTo(b.Plan.PipeCount);
            if (c != 0)
            {
                return c;
            }

            // prefer solutions that more algorithms find
            c = b.GroupSize.CompareTo(a.GroupSize);
            if (c != 0)
            {
                return c;
            }

            // the rest of the sorting is for arbitrary tie breaking
            c = a.Plan.PipeStrategy.CompareTo(b.Plan.PipeStrategy);
            if (c != 0)
            {
                return c;
            }

            c = a.Plan.OptimizePipes.CompareTo(b.Plan.OptimizePipes);
            if (c != 0)
            {
                return c;
            }

            c = Comparer<BeaconStrategy?>.Default.Compare(a.Plan.BeaconStrategy, b.Plan.BeaconStrategy);
            if (c != 0)
            {
                return c;
            }

            return a.GroupNumber.CompareTo(b.GroupNumber);
        });

        PlanInfo? bestPlanInfo = null;
        var noMoreAlternates = false;
        var selectedPlans = new List<OilFieldPlan>();
        var alternatePlans = new List<OilFieldPlan>();
        var unusedPlans = new List<OilFieldPlan>();

        foreach (var planInfo in sortedPlans)
        {
            if (context.Options.AddHeatPipes && !IsHeatPipeCompatible(context, planInfo))
            {
                // Heat is a planning constraint on Aquilo, not a best-effort post-processing step. Skip layouts
                // whose pipe/beacon placement leaves no connected heat-pipe solution.
                continue;
            }

            if (noMoreAlternates)
            {
                unusedPlans.Add(planInfo.Plan);
                continue;
            }
            else if (bestPlanInfo is null)
            {
                bestPlanInfo = planInfo;
                selectedPlans.Add(planInfo.Plan);
                continue;
            }

            var (bestGroupNumber, _, bestPlan, _, _) = bestPlanInfo;
            if (planInfo.Plan.IsEquivalent(bestPlan))
            {
                if (planInfo.GroupNumber == bestGroupNumber)
                {
                    selectedPlans.Add(planInfo.Plan);
                }
                else
                {
                    alternatePlans.Add(planInfo.Plan);
                }
            }
            else
            {
                noMoreAlternates = true;
                unusedPlans.Add(planInfo.Plan);
            }
        }

        if (bestPlanInfo is null)
        {
            throw new FactorioToolsException("No heat-compatible pipe layout could be found.");
        }

        return Result.NewData(new SolutionInfo(selectedPlans, alternatePlans, unusedPlans, bestPlanInfo.Pipes, bestPlanInfo.Beacons));
    }

    /// <summary>
    /// A conservative Aquilo fallback. Unlike the legacy strategies, it selects each pumpjack terminal and fluid
    /// path only after confirming that the partial layout can still be heated. It provides a valid baseline for the
    /// later quality optimisers rather than declaring a dense field impossible.
    /// </summary>
    private static SolutionInfo? TryGetHeatAwareFallback(Context context)
    {
        // Cap the fallback so an unusually dense field remains responsive in the browser. The ordered alternatives
        // below cover each terminal orientation and four orthogonal route shapes before this limit is reached.
        var budget = 200;
        var state = SearchHeatAwareFallback(
            context,
            new PipeGrid(context.Grid),
            context.Centers.ToList(),
            context.GetLocationSet(allowEnumerate: true),
            new Dictionary<Location, TerminalLocation>(),
            ref budget);
        if (state is not null)
        {
            return MakeHeatAwareFallbackSolution(context, state);
        }

        // Different growth orders expose different valid terminal/orientation combinations. Keep this deterministic
        // while exploring the principal alternatives before escalating to a deeper search.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = TryGetHeatAwareFallback(context, attempt);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private sealed record HeatFallbackState(PipeGrid Grid, ILocationSet Pipes, Dictionary<Location, TerminalLocation> Terminals);

    private static HeatFallbackState? SearchHeatAwareFallback(
        Context context,
        PipeGrid grid,
        List<Location> remaining,
        ILocationSet pipes,
        Dictionary<Location, TerminalLocation> terminals,
        ref int budget)
    {
        if (budget-- == 0)
        {
            return null;
        }
        if (remaining.Count == 0)
        {
            return new HeatFallbackState(grid, pipes, terminals);
        }

        var center = remaining.OrderBy(location => context.CenterToTerminals[location].Count).First();
        remaining.Remove(center);
        foreach (var terminal in context.CenterToTerminals[center])
        {
            if (!grid.IsEmpty(terminal.Terminal))
            {
                continue;
            }
            for (var routeVariant = 0; routeVariant < (pipes.Count == 0 ? 1 : 4); routeVariant++)
            {
                List<Location> path;
                if (pipes.Count == 0)
                {
                    path = new List<Location> { terminal.Terminal };
                }
                else
                {
                    var pathResult = GetFluidPath(grid, terminal.Terminal, pipes, routeVariant);
                    if (pathResult is null)
                    {
                        continue;
                    }
                    path = pathResult;
                }
                var candidateGrid = new PipeGrid(grid);
                var candidatePipes = context.GetLocationSet(pipes);
                candidateGrid.AddEntity(terminal.Terminal, new Terminal(candidateGrid.GetId()));
                foreach (var location in path)
                {
                    if (candidateGrid.IsEmpty(location)) candidateGrid.AddEntity(location, new Pipe(candidateGrid.GetId()));
                    candidatePipes.Add(location);
                }
                if (!AddHeatPipes.CanPlan(candidateGrid)) continue;
                var candidateTerminals = new Dictionary<Location, TerminalLocation>(terminals) { [center] = terminal };
                var result = SearchHeatAwareFallback(context, candidateGrid, remaining, candidatePipes, candidateTerminals, ref budget);
                if (result is not null) return result;
            }
        }
        remaining.Add(center);
        return null;
    }

    private static SolutionInfo MakeHeatAwareFallbackSolution(Context context, HeatFallbackState state)
    {
        var centerToTerminals = context.GetLocationDictionary<List<TerminalLocation>>();
        var locationToTerminals = context.GetLocationDictionary<List<TerminalLocation>>();
        foreach (var pair in state.Terminals)
        {
            centerToTerminals.Add(pair.Key, new List<TerminalLocation> { pair.Value });
            locationToTerminals.Add(pair.Value.Terminal, new List<TerminalLocation> { pair.Value });
        }
        var solution = new Solution { Strategies = new List<PipeStrategy> { PipeStrategy.ConnectedCentersDelaunay }, Optimized = new List<bool> { false }, CenterToConnectedCenters = null, CenterToTerminals = centerToTerminals, LocationToTerminals = locationToTerminals, PipeCountWithoutUnderground = state.Pipes.Count, Pipes = state.Pipes, UndergroundPipes = null, BeaconSolutions = null };
        var plan = new OilFieldPlan(PipeStrategy.ConnectedCentersDelaunay, false, null, 0, 0, state.Pipes.Count, state.Pipes.Count);
        return new SolutionInfo(new List<OilFieldPlan> { plan }, new List<OilFieldPlan>(), new List<OilFieldPlan>(), solution, null);
    }

    private static SolutionInfo? TryGetHeatAwareFallback(Context context, int attempt)
    {
        var grid = new PipeGrid(context.Grid);
        var pipes = context.GetLocationSet(allowEnumerate: true);
        var centerToTerminals = context.GetLocationDictionary<List<TerminalLocation>>();
        var locationToTerminals = context.GetLocationDictionary<List<TerminalLocation>>();

        var centers = attempt switch
        {
            0 => context.Centers.OrderBy(center => context.CenterToTerminals[center].Count).ThenBy(center => center.GetManhattanDistance(context.Grid.Middle)).ToList(),
            1 => context.Centers.OrderByDescending(center => context.CenterToTerminals[center].Count).ThenBy(center => center.GetManhattanDistance(context.Grid.Middle)).ToList(),
            2 => context.Centers.OrderBy(center => center.X).ThenBy(center => center.Y).ToList(),
            3 => context.Centers.OrderByDescending(center => center.X).ThenBy(center => center.Y).ToList(),
            4 => context.Centers.OrderBy(center => center.Y).ThenBy(center => center.X).ToList(),
            5 => context.Centers.OrderByDescending(center => center.Y).ThenBy(center => center.X).ToList(),
            _ => context.Centers.OrderByDescending(center => center.GetManhattanDistance(context.Grid.Middle)).ToList(),
        };

        foreach (var center in centers)
        {
            TerminalLocation? selected = null;
            IReadOnlyList<Location>? selectedPath = null;

            var terminals = attempt % 2 == 0
                ? context.CenterToTerminals[center].OrderBy(terminal => terminal.Direction)
                : context.CenterToTerminals[center].OrderByDescending(terminal => terminal.Direction);
            foreach (var terminal in terminals)
            {
                if (!grid.IsEmpty(terminal.Terminal))
                {
                    continue;
                }

                IReadOnlyList<Location> path;
                if (pipes.Count == 0)
                {
                    path = new[] { terminal.Terminal };
                }
                else
                {
                    var result = GetFluidPath(grid, terminal.Terminal, pipes);
                    if (result is null)
                    {
                        continue;
                    }

                    path = result;
                }

                var candidateGrid = new PipeGrid(grid);
                candidateGrid.AddEntity(terminal.Terminal, new Terminal(candidateGrid.GetId()));
                foreach (var location in path)
                {
                    if (candidateGrid.IsEmpty(location))
                    {
                        candidateGrid.AddEntity(location, new Pipe(candidateGrid.GetId()));
                    }
                }

                if (!AddHeatPipes.CanPlan(candidateGrid))
                {
                    continue;
                }

                if (selectedPath is null || path.Count < selectedPath.Count)
                {
                    selected = terminal;
                    selectedPath = path;
                }
            }

            if (selected is null || selectedPath is null)
            {
                return null;
            }

            grid.AddEntity(selected.Terminal, new Terminal(grid.GetId()));
            foreach (var location in selectedPath)
            {
                if (grid.IsEmpty(location))
                {
                    grid.AddEntity(location, new Pipe(grid.GetId()));
                }
                pipes.Add(location);
            }

            centerToTerminals.Add(center, new List<TerminalLocation> { selected });
            locationToTerminals.Add(selected.Terminal, new List<TerminalLocation> { selected });
        }

        var solution = new Solution
        {
            Strategies = new List<PipeStrategy> { PipeStrategy.ConnectedCentersDelaunay },
            Optimized = new List<bool> { false },
            CenterToConnectedCenters = null,
            CenterToTerminals = centerToTerminals,
            LocationToTerminals = locationToTerminals,
            PipeCountWithoutUnderground = pipes.Count,
            Pipes = pipes,
            UndergroundPipes = null,
            BeaconSolutions = null,
        };

        var plan = new OilFieldPlan(PipeStrategy.ConnectedCentersDelaunay, false, null, 0, 0, pipes.Count, pipes.Count);
        return new SolutionInfo(new List<OilFieldPlan> { plan }, new List<OilFieldPlan>(), new List<OilFieldPlan>(), solution, null);
    }

    /// <summary>
    /// Finds a fluid route to an already-placed pipe. PipeGrid deliberately exposes only empty neighbours, which is
    /// correct for most existing algorithms but means A* cannot step onto an occupied goal. The Aquilo baseline
    /// grows a real grid as it searches, so it needs this small goal-aware traversal.
    /// </summary>
    private static List<Location>? GetFluidPath(PipeGrid grid, Location start, ILocationSet goals, int routeVariant = 0)
    {
        var queue = new Queue<Location>();
        var visited = new HashSet<Location> { start };
        var previous = new Dictionary<Location, Location>();
        queue.Enqueue(start);

        Span<Location> adjacent = stackalloc Location[4];
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            grid.GetAdjacent(adjacent, current);
            for (var offset = 0; offset < adjacent.Length; offset++)
            {
                var i = (offset + routeVariant) % adjacent.Length;
                var next = adjacent[i];
                if (!next.IsValid || !visited.Add(next))
                {
                    continue;
                }

                if (!grid.IsEmpty(next) && !goals.Contains(next))
                {
                    continue;
                }

                previous[next] = current;
                if (goals.Contains(next))
                {
                    var path = new List<Location>();
                    for (var location = next; ; location = previous[location])
                    {
                        path.Add(location);
                        if (location == start)
                        {
                            break;
                        }
                    }

                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static bool IsHeatPipeCompatible(Context context, PlanInfo planInfo)
    {
        var grid = new PipeGrid(context.Grid);

        foreach (var terminals in planInfo.Pipes.CenterToTerminals.Values)
        {
            foreach (var terminal in terminals)
            {
                if (grid.IsEmpty(terminal.Terminal))
                {
                    grid.AddEntity(terminal.Terminal, new Terminal(grid.GetId()));
                }
            }
        }

        if (planInfo.Pipes.UndergroundPipes is not null)
        {
            foreach ((var location, var direction) in planInfo.Pipes.UndergroundPipes.EnumeratePairs())
            {
                if (grid.IsEmpty(location))
                {
                    grid.AddEntity(location, new UndergroundPipe(grid.GetId(), direction));
                }
            }
        }

        foreach (var location in planInfo.Pipes.Pipes.EnumerateItems())
        {
            if (grid.IsEmpty(location))
            {
                grid.AddEntity(location, new Pipe(grid.GetId()));
            }
        }

        if (planInfo.Beacons is not null)
        {
            AddBeaconsToGrid(grid, context.Options, planInfo.Beacons.Beacons);
        }

        return AddHeatPipes.CanPlan(grid);
    }

    private static Result<List<PlanInfo>> GetAllPlans(Context context)
    {
        var result = GetSolutionGroups(context);
        if (result.Exception is not null)
        {
            return Result.NewException<List<PlanInfo>>(result.Exception);
        }

        var solutionGroups = result.Data!;
        var plans = new List<PlanInfo>();
        foreach ((var solutionGroup, var groupNumber) in solutionGroups)
        {
            foreach (var solution in solutionGroup)
            {
                if (solution.BeaconSolutions is null)
                {
                    foreach (var strategy in solution.Strategies)
                    {
                        foreach (var optimized in solution.Optimized)
                        {
                            var plan = new OilFieldPlan(
                                strategy,
                                optimized,
                                BeaconStrategy: null,
                                BeaconEffectCount: 0,
                                BeaconCount: 0,
                                solution.Pipes.Count,
                                solution.PipeCountWithoutUnderground);

                            plans.Add(new PlanInfo(groupNumber, solutionGroup.Count, plan, solution, null));
                        }
                    }
                }
                else
                {
                    foreach (var beacons in solution.BeaconSolutions)
                    {
                        foreach (var strategy in solution.Strategies)
                        {
                            foreach (var optimized in solution.Optimized)
                            {
                                var plan = new OilFieldPlan(
                                    strategy,
                                    optimized,
                                    beacons.Strategy,
                                    beacons.Effects,
                                    beacons.Beacons.Count,
                                    solution.Pipes.Count,
                                    solution.PipeCountWithoutUnderground);

                                plans.Add(new PlanInfo(groupNumber, solutionGroup.Count, plan, solution, beacons));
                            }
                        }
                    }
                }
            }
        }

        return Result.NewData(plans);
    }

    private static Result<IReadOnlyCollection<SolutionsAndGroupNumber>> GetSolutionGroups(Context context)
    {
        var originalCenterToTerminals = context.CenterToTerminals;
        var originalLocationToTerminals = context.LocationToTerminals;

        var pipesToSolutions = new Dictionary<ILocationSet, SolutionsAndGroupNumber>(LocationSetComparer.Instance);
        var connectedCentersToSolutions = new Dictionary<ILocationDictionary<ILocationSet>, List<Solution>>(ConnectedCentersComparer.Instance);

        if (context.CenterToTerminals.Count == 1)
        {
            var terminal = context.CenterToTerminals.EnumeratePairs().Single().Value[0];
            EliminateOtherTerminals(context, terminal);
            var pipes = context.GetSingleLocationSet(terminal.Terminal);
            var solutions = OptimizeAndAddSolutions(context, pipesToSolutions, default, pipes, centerToConnectedCenters: null);
            var solution = solutions.Single();
            solution.Strategies.Clear();
            solution.Strategies.AddRange(context.Options.PipeStrategies);
        }
        else
        {
            var completedStrategies = new CountedBitArray((int)PipeStrategy.ConnectedCentersFlute + 1); // max value
            foreach (var strategy in context.Options.PipeStrategies)
            {
                if (completedStrategies[(int)strategy])
                {
                    continue;
                }

                context.CenterToTerminals = originalCenterToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());
                context.LocationToTerminals = originalLocationToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());

                switch (strategy)
                {
                    case PipeStrategy.FbeOriginal:
                    case PipeStrategy.Fbe:
                        {
                            var result = AddPipesFbe.Execute(context, strategy);
                            if (result.Exception is not null)
                            {
                                return Result.NewException<IReadOnlyCollection<SolutionsAndGroupNumber>>(result.Exception);
                            }

                            (var pipes, var finalStrategy) = result.Data!;
                            completedStrategies[(int)finalStrategy] = true;

                            OptimizeAndAddSolutions(context, pipesToSolutions, finalStrategy, pipes, centerToConnectedCenters: null);
                        }
                        break;

                    case PipeStrategy.ConnectedCentersDelaunay:
                    case PipeStrategy.ConnectedCentersDelaunayMst:
                    case PipeStrategy.ConnectedCentersFlute:
                        {
                            ILocationDictionary<ILocationSet> centerToConnectedCenters = AddPipesConnectedCenters.GetConnectedPumpjacks(context, strategy);
                            completedStrategies[(int)strategy] = true;

                            if (connectedCentersToSolutions.TryGetValue(centerToConnectedCenters, out var solutions))
                            {
                                foreach (var solution in solutions)
                                {
                                    solution.Strategies.Add(strategy);
                                }
                                continue;
                            }

                            var result = AddPipesConnectedCenters.FindTrunksAndConnect(context, centerToConnectedCenters);
                            if (result.Exception is not null)
                            {
                                return Result.NewException<IReadOnlyCollection<SolutionsAndGroupNumber>>(result.Exception);
                            }

                            var pipes = result.Data!;
                            solutions = OptimizeAndAddSolutions(context, pipesToSolutions, strategy, pipes, centerToConnectedCenters);
                            connectedCentersToSolutions.Add(centerToConnectedCenters, solutions);
                        }
                        break;

                    default:
                        throw new NotImplementedException();
                }
            }
        }

        return Result.NewData<IReadOnlyCollection<SolutionsAndGroupNumber>>(pipesToSolutions.Values);
    }

    private static List<Solution> OptimizeAndAddSolutions(
        Context context,
        Dictionary<ILocationSet, SolutionsAndGroupNumber> pipesToSolutions,
        PipeStrategy strategy,
        ILocationSet pipes,
        ILocationDictionary<ILocationSet>? centerToConnectedCenters)
    {
        SolutionsAndGroupNumber? solutionsAndIndex;
        if (pipesToSolutions.TryGetValue(pipes, out solutionsAndIndex))
        {
            foreach (var solution in solutionsAndIndex.Solutions)
            {
                solution.Strategies.Add(strategy);
            }

            return solutionsAndIndex.Solutions;
        }

        // Visualizer.Show(context.Grid, pipes.Select(p => (IPoint)new Point(p.X, p.Y)), Array.Empty<IEdge>());

        var originalCenterToTerminals = context.CenterToTerminals;
        var originalLocationToTerminals = context.LocationToTerminals;

        ILocationSet optimizedPipes = pipes;
        if (context.Options.OptimizePipes)
        {
            context.CenterToTerminals = originalCenterToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());
            context.LocationToTerminals = originalLocationToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());
            optimizedPipes = context.GetLocationSet(pipes);
            RotateOptimize.Execute(context, optimizedPipes);

            // Visualizer.Show(context.Grid, optimizedPipes.Select(p => (IPoint)new Point(p.X, p.Y)), Array.Empty<IEdge>());
        }

        List<Solution> solutions;
        if (pipes.SetEquals(optimizedPipes))
        {
            optimizedPipes = context.Options.UseUndergroundPipes ? context.GetLocationSet(pipes) : pipes;
            solutions = new List<Solution>
            {
                GetSolution(context, strategy, optimized: false, centerToConnectedCenters, optimizedPipes)
            };

            if (context.Options.OptimizePipes)
            {
                solutions[0].Optimized.Add(true);
            }
        }
        else
        {
            var solutionA = GetSolution(context, strategy, optimized: true, centerToConnectedCenters, optimizedPipes);

            context.CenterToTerminals = originalCenterToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());
            context.LocationToTerminals = originalLocationToTerminals.EnumeratePairs().ToDictionary(context, x => x.Key, x => x.Value.ToList());
            var pipesB = context.Options.UseUndergroundPipes ? context.GetLocationSet(pipes) : pipes;
            var solutionB = GetSolution(context, strategy, optimized: false, centerToConnectedCenters, pipesB);

            Validate.PipesDoNotMatch(context, solutionA.Pipes, solutionB.Pipes);

            solutions = new List<Solution> { solutionA, solutionB };
        }

        pipesToSolutions.Add(pipes, new SolutionsAndGroupNumber(solutions, pipesToSolutions.Count + 1));

        return solutions;
    }

    private record SolutionsAndGroupNumber(List<Solution> Solutions, int GroupNumber);

    private static Solution GetSolution(
        Context context,
        PipeStrategy strategy,
        bool optimized,
        ILocationDictionary<ILocationSet>? centerToConnectedCenters,
        ILocationSet optimizedPipes)
    {
        Validate.PipesAreConnected(context, optimizedPipes);

        var pipeCountBeforeUnderground = optimizedPipes.Count;

        ILocationDictionary<Direction>? undergroundPipes = null;
        if (context.Options.UseUndergroundPipes)
        {
            undergroundPipes = PlanUndergroundPipes.Execute(context, optimizedPipes);
        }

        List<BeaconSolution>? beaconSolutions = null;
        if (context.Options.AddBeacons)
        {
            beaconSolutions = PlanBeacons.Execute(context, optimizedPipes);
        }

        Validate.NoOverlappingEntities(context, optimizedPipes, undergroundPipes, beaconSolutions);

        // Visualizer.Show(context.Grid, optimizedPipes.Select(p => (DelaunatorSharp.IPoint)new DelaunatorSharp.Point(p.X, p.Y)), Array.Empty<DelaunatorSharp.IEdge>());

        return new Solution
        {
            Strategies = new List<PipeStrategy> { strategy },
            Optimized = new List<bool> { optimized },
            CenterToConnectedCenters = centerToConnectedCenters,
            // Each candidate may select different pumpjack terminals. Preserve that snapshot: later strategies and
            // orientation optimisation mutate the working dictionaries, and reusing those dictionaries makes a heat
            // feasibility check evaluate the wrong layout.
            CenterToTerminals = context.CenterToTerminals.EnumeratePairs().ToDictionary(context, pair => pair.Key, pair => pair.Value.ToList()),
            LocationToTerminals = context.LocationToTerminals.EnumeratePairs().ToDictionary(context, pair => pair.Key, pair => pair.Value.ToList()),
            PipeCountWithoutUnderground = pipeCountBeforeUnderground,
            Pipes = optimizedPipes,
            UndergroundPipes = undergroundPipes,
            BeaconSolutions = beaconSolutions,
        };
    }

    private static void EliminateStrandedTerminals(Context context)
    {
        var locationsToExplore = context.LocationToTerminals.Keys.ToReadOnlySet(context, allowEnumerate: true);

        while (locationsToExplore.Count > 0)
        {
            var goals = context.GetLocationSet(locationsToExplore);
            var start = goals.EnumerateItems().First();
            goals.Remove(start);

            var result = Dijkstras.GetShortestPaths(context, context.Grid, start, goals, stopOnFirstGoal: false, allowGoalEnumerate: true);

            var reachedTerminals = result.ReachedGoals;
            reachedTerminals.Add(start);

            var unreachedTerminals = context.GetLocationSet(goals);
            unreachedTerminals.ExceptWith(result.ReachedGoals);

            var reachedPumpjacks = context.GetLocationSet();
            foreach (var location in result.ReachedGoals.EnumerateItems())
            {
                var terminals = context.LocationToTerminals[location];
                for (var i = 0; i < terminals.Count; i++)
                {
                    reachedPumpjacks.Add(terminals[i].Center);
                }
            }

            ILocationSet terminalsToEliminate;
            if (reachedPumpjacks.Count == context.CenterToTerminals.Count)
            {
                terminalsToEliminate = unreachedTerminals;
                locationsToExplore.Clear();
            }
            else
            {
                terminalsToEliminate = reachedTerminals;
                locationsToExplore = unreachedTerminals;
            }

            Location strandedTerminal = Location.Invalid;
            bool foundStranded = false;
            foreach (var location in terminalsToEliminate.EnumerateItems())
            {
                foreach (var terminal in context.LocationToTerminals[location])
                {
                    var terminals = context.CenterToTerminals[terminal.Center];
                    terminals.Remove(terminal);

                    if (terminals.Count == 0)
                    {
                        strandedTerminal = terminal.Terminal;
                        foundStranded = true;
                    }
                }

                context.LocationToTerminals.Remove(location);
            }
            
            if (foundStranded)
            {
                /*
                var clone = new PipeGrid(context.Grid);
                AddPipeEntities.Execute(clone, new(), context.CenterToTerminals, new ILocationSet(), undergroundPipes: null, allowMultipleTerminals: true);
                Visualizer.Show(clone, new[] { strandedTerminal.Value, locationsToExplore.First() }.Select(x => (IPoint)new Point(x.X, x.Y)), Array.Empty<IEdge>());
                */

                throw new NoPathBetweenTerminalsException(strandedTerminal, locationsToExplore.EnumerateItems().First());
            }
        }
    }

    private class Solution
    {
        public required List<PipeStrategy> Strategies { get; set; }
        public required List<bool> Optimized { get; set; }
        public required ILocationDictionary<ILocationSet>? CenterToConnectedCenters { get; set; }
        public required ILocationDictionary<List<TerminalLocation>> CenterToTerminals { get; set; }
        public required ILocationDictionary<List<TerminalLocation>> LocationToTerminals { get; set; }
        public required int PipeCountWithoutUnderground { get; set; }
        public required ILocationSet Pipes { get; set; }
        public required ILocationDictionary<Direction>? UndergroundPipes { get; set; }
        public required List<BeaconSolution>? BeaconSolutions { get; set; }
    }

    private class LocationSetComparer : IEqualityComparer<ILocationSet>
    {
        public static readonly LocationSetComparer Instance = new LocationSetComparer();

        public bool Equals(ILocationSet? x, ILocationSet? y)
        {
            if (x is null && y is null)
            {
                return true;
            }

            if (x is null)
            {
                return false;
            }

            if (y is null)
            {
                return false;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            return x.SetEquals(y);
        }

        public int GetHashCode([DisallowNull] ILocationSet obj)
        {
            var sumX = 0;
            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var sumY = 0;
            var minY = int.MaxValue;
            var maxY = int.MinValue;

            foreach (var l in obj.EnumerateItems())
            {
                sumX += l.X;

                if (l.X < minX)
                {
                    minX = l.X;
                }

                if (l.X > maxX)
                {
                    maxX = l.X;
                }

                sumY += l.Y;

                if (l.Y < minY)
                {
                    minY = l.Y;
                }

                if (l.Y > maxY)
                {
                    maxY = l.Y;
                }
            }

            var hash = 17;
            hash = hash * 23 + obj.Count;
            hash = hash * 23 + sumX;
            hash = hash * 23 + minX;
            hash = hash * 23 + maxX;
            hash = hash * 23 + sumY;
            hash = hash * 23 + minY;
            hash = hash * 23 + maxY;

            return hash;
        }
    }

    private class ConnectedCentersComparer : IEqualityComparer<ILocationDictionary<ILocationSet>>
    {
        public static readonly ConnectedCentersComparer Instance = new ConnectedCentersComparer();

        public bool Equals(ILocationDictionary<ILocationSet>? x, ILocationDictionary<ILocationSet>? y)
        {
            if (x is null && y is null)
            {
                return true;
            }

            if (x is null)
            {
                return false;
            }

            if (y is null)
            {
                return false;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            foreach ((var key, var xValue) in x.EnumeratePairs())
            {
                if (!y.TryGetValue(key, out var yValue))
                {
                    return false;
                }

                if (!xValue.SetEquals(yValue))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode([DisallowNull] ILocationDictionary<ILocationSet> obj)
        {
            var sumX = 0;
            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var sumY = 0;
            var minY = int.MaxValue;
            var maxY = int.MinValue;
            var locationSum = 0;

            foreach (var (l, s) in obj.EnumeratePairs())
            {
                sumX += l.X;

                if (l.X < minX)
                {
                    minX = l.X;
                }

                if (l.X > maxX)
                {
                    maxX = l.X;
                }

                sumY += l.Y;

                if (l.Y < minY)
                {
                    minY = l.Y;
                }

                if (l.Y > maxY)
                {
                    maxY = l.Y;
                }

                locationSum = s.Count;
            }

            var hash = 17;
            hash = hash * 23 + obj.Count;
            hash = hash * 23 + sumX;
            hash = hash * 23 + minX;
            hash = hash * 23 + maxX;
            hash = hash * 23 + sumY;
            hash = hash * 23 + minY;
            hash = hash * 23 + maxY;
            hash = hash * 23 + locationSum;

            return hash;
        }
    }

    private record PlanInfo(int GroupNumber, int GroupSize, OilFieldPlan Plan, Solution Pipes, BeaconSolution? Beacons);
}
