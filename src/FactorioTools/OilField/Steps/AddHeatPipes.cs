using System;
using System.Collections.Generic;
using System.Linq;

namespace Knapcode.FactorioTools.OilField;

/// <summary>
/// Adds one orthogonally connected heat-pipe network. Aquilo entities must directly share an edge with a hot heat
/// pipe; diagonal proximity is not sufficient. The network is deliberately left exposed so it can be connected to a
/// heating tower or reactor.
/// </summary>
public static class AddHeatPipes
{
    private sealed class HeatTarget
    {
        public HeatTarget(int id)
        {
            Id = id;
        }

        public int Id { get; }
        public List<Location> Locations { get; } = new();
    }

    public static void Execute(Context context)
    {
        var heatPipes = TryPlan(context.Grid);
        if (heatPipes is null)
        {
            throw new FactorioToolsException("No heat-pipe-compatible layout could be found for Aquilo.", badInput: true);
        }

        foreach (var location in heatPipes)
        {
            context.Grid.AddEntity(location, new HeatPipe(context.Grid.GetId()));
        }

        if (context.Options.ValidateSolution)
        {
            foreach (var target in GetTargets(context.Grid))
            {
                if (!IsHeated(target, heatPipes))
                {
                    throw new FactorioToolsException("An Aquilo entity is not adjacent to a heat pipe.");
                }
            }

            Validate.HeatPipesAreConnected(context);
        }
    }

    /// <summary>
    /// Reserves the initial connected network before fluid pipes and beacons are planned. Those planners therefore
    /// route around heat instead of forcing heat to fit through whatever space they happen to leave afterwards.
    /// </summary>
    public static void ReserveForPumpjacks(Context context)
    {
        var terminals = new HashSet<Location>(context.LocationToTerminals.Keys);
        var heatPipes = TryPlan(context.Grid, excludedLocations: terminals);
        if (heatPipes is null)
        {
            throw new FactorioToolsException("No heat-pipe-compatible layout could be found for Aquilo.", badInput: true);
        }

        foreach (var location in heatPipes)
        {
            if (context.Grid.IsEmpty(location))
            {
                context.Grid.AddEntity(location, new HeatPipe(context.Grid.GetId()));
            }
        }
    }

    /// <summary>
    /// Determines whether a planned pipe and beacon layout can accommodate the mandatory Aquilo heat network.
    /// This is used while choosing a layout, before it is committed to the planner grid.
    /// </summary>
    public static bool CanPlan(SquareGrid grid) => TryPlan(grid) is not null;

    /// <summary>
    /// Plans a heat spine for the pumpjacks and the proposed ordinary pipes. It is used before beacons are placed,
    /// making the required network a reservation the beacon planner must work around.
    /// </summary>
    public static IReadOnlyCollection<Location>? PlanForPipes(SquareGrid grid, ILocationSet pipes)
        => TryPlan(grid, pipes.EnumerateItems());

    /// <summary>
    /// Checks a prospective beacon placement without changing the working grid. Beacon planners use this while
    /// choosing candidates, so that they leave space for the mandatory heat network instead of merely discovering
    /// the conflict after all beacons have been placed.
    /// </summary>
    private static HashSet<Location>? TryPlan(
        SquareGrid grid,
        IEnumerable<Location>? additionalPipeLocations = null,
        IReadOnlySet<Location>? excludedLocations = null)
    {
        var targets = GetTargets(grid, additionalPipeLocations);
        var targetToCandidates = new Dictionary<HeatTarget, List<Location>>(targets.Count);
        foreach (var target in targets)
        {
            var candidates = GetCandidates(grid, target, excludedLocations);
            if (candidates.Count == 0)
            {
                return null;
            }

            targetToCandidates.Add(target, candidates);
        }

        var existingHeatPipes = grid.EntityLocations
            .EnumerateItems()
            .Where(location => grid[location] is HeatPipe)
            .ToHashSet();
        if (targets.Count == 0)
        {
            return existingHeatPipes;
        }

        // Different first branches make materially different spines in dense oil fields.  The old implementation
        // tried one branch and could therefore report a layout impossible even though moving the backbone solved it.
        // Explore a bounded, deterministic selection of starts; each attempt remains a linear-time grid search.
        var starts = targets
            .OrderBy(target => targetToCandidates[target].Count)
            .ThenBy(target => target.Locations.Min(location => location.GetManhattanDistance(grid.Middle)))
            .Take(8)
            .SelectMany(target => targetToCandidates[target]
                .OrderBy(location => location.GetManhattanDistance(grid.Middle))
                .ThenBy(location => location.Y)
                .ThenBy(location => location.X)
                .Take(8)
                .Select(location => (target, location)))
            .ToList();

        foreach (var (firstTarget, firstLocation) in starts)
        {
            var result = TryPlanFromStart(grid, targets, targetToCandidates, existingHeatPipes, firstTarget, firstLocation);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static HashSet<Location>? TryPlanFromStart(
        SquareGrid grid,
        List<HeatTarget> targets,
        Dictionary<HeatTarget, List<Location>> targetToCandidates,
        HashSet<Location> existingHeatPipes,
        HeatTarget firstTarget,
        Location firstLocation)
    {
        var heatPipes = new HashSet<Location>(existingHeatPipes) { firstLocation };
        var remainingTargets = new HashSet<HeatTarget>(targets);
        remainingTargets.Remove(firstTarget);

        while (remainingTargets.Count > 0)
        {
            var targetsAlreadyHeated = remainingTargets.Where(target => IsHeated(target, heatPipes)).ToList();
            if (targetsAlreadyHeated.Count > 0)
            {
                foreach (var target in targetsAlreadyHeated)
                {
                    remainingTargets.Remove(target);
                }

                continue;
            }

            // One breadth-first search finds the closest candidate belonging to any remaining target. The previous
            // implementation ran a full search for every target at every branch, which made large fields needlessly
            // slow while offering no better route choice.
            var candidateToTarget = new Dictionary<Location, HeatTarget>();
            foreach (var target in remainingTargets.OrderBy(target => targetToCandidates[target].Count))
            {
                foreach (var candidate in targetToCandidates[target])
                {
                    candidateToTarget.TryAdd(candidate, target);
                }
            }

            var path = FindShortestPath(grid, heatPipes, candidateToTarget.Keys);
            if (path is null || path.Count == 0)
            {
                return null;
            }

            heatPipes.UnionWith(path);
            remainingTargets.Remove(candidateToTarget[path[0]]);
        }

        // A finished Aquilo network must not be a sealed island inside the generated layout. Reserve a route to the
        // edge of the planning grid so the player can join it to a heating tower or reactor outside the blueprint.
        var outside = GetOutsideDestinations(grid);
        var outsidePath = FindShortestPath(grid, heatPipes, outside);
        if (outsidePath is null)
        {
            return null;
        }

        heatPipes.UnionWith(outsidePath);

        return heatPipes;
    }

    private static IReadOnlyCollection<Location> GetOutsideDestinations(SquareGrid grid)
    {
        var destinations = new List<Location>();
        for (var x = 0; x < grid.Width; x++)
        {
            var top = new Location(x, 0);
            var bottom = new Location(x, grid.Height - 1);
            if (grid.IsEmpty(top))
            {
                destinations.Add(top);
            }
            if (grid.IsEmpty(bottom))
            {
                destinations.Add(bottom);
            }
        }

        for (var y = 1; y < grid.Height - 1; y++)
        {
            var left = new Location(0, y);
            var right = new Location(grid.Width - 1, y);
            if (grid.IsEmpty(left))
            {
                destinations.Add(left);
            }
            if (grid.IsEmpty(right))
            {
                destinations.Add(right);
            }
        }

        return destinations;
    }

    private static List<HeatTarget> GetTargets(SquareGrid grid, IEnumerable<Location>? additionalPipeLocations = null)
    {
        var idToTarget = new Dictionary<int, HeatTarget>();

        foreach (var location in grid.EntityLocations.EnumerateItems())
        {
            var entity = grid[location];
            var id = entity switch
            {
                PumpjackCenter pumpjack => pumpjack.Id,
                PumpjackSide pumpjack => pumpjack.Center.Id,
                BeaconCenter beacon => beacon.Id,
                BeaconSide beacon => beacon.Center.Id,
                Pipe pipe => pipe.Id,
                _ => 0,
            };

            if (id == 0)
            {
                continue;
            }

            if (!idToTarget.TryGetValue(id, out var target))
            {
                target = new HeatTarget(id);
                idToTarget.Add(id, target);
            }

            target.Locations.Add(location);
        }

        if (additionalPipeLocations is not null)
        {
            var plannedPipeId = int.MinValue;
            foreach (var location in additionalPipeLocations)
            {
                // The planned pipe locations are not on the working grid while beacon candidates are generated.
                // Heat each one as an ordinary pipe would be heated in the completed plan.
                while (idToTarget.ContainsKey(plannedPipeId))
                {
                    plannedPipeId++;
                }

                var target = new HeatTarget(plannedPipeId++);
                target.Locations.Add(location);
                idToTarget.Add(target.Id, target);
            }
        }

        return idToTarget.Values
            .OrderBy(t => t.Locations.Min(l => l.Y))
            .ThenBy(t => t.Locations.Min(l => l.X))
            .ToList();
    }

    private static bool IsHeated(HeatTarget target, HashSet<Location> heatPipes)
    {
        foreach (var location in target.Locations)
        {
            if (heatPipes.Contains(location.Translate(1, 0))
                || heatPipes.Contains(location.Translate(-1, 0))
                || heatPipes.Contains(location.Translate(0, 1))
                || heatPipes.Contains(location.Translate(0, -1)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Location> GetCandidates(SquareGrid grid, HeatTarget target, IReadOnlySet<Location>? excludedLocations = null)
    {
        var candidates = new HashSet<Location>();
        foreach (var location in target.Locations)
        {
            AddCandidate(location.Translate(1, 0));
            AddCandidate(location.Translate(-1, 0));
            AddCandidate(location.Translate(0, 1));
            AddCandidate(location.Translate(0, -1));
        }

        return candidates.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();

        void AddCandidate(Location candidate)
        {
            if (grid.IsInBounds(candidate)
                && grid.IsEmpty(candidate)
                && (excludedLocations is null || !excludedLocations.Contains(candidate)))
            {
                candidates.Add(candidate);
            }
        }
    }

    private static IReadOnlyList<Location>? FindShortestPath(
        SquareGrid grid,
        HashSet<Location> heatPipes,
        IReadOnlyCollection<Location> destinations)
    {
        var queue = new Queue<Location>();
        var visited = new HashSet<Location>(heatPipes);
        var previous = new Dictionary<Location, Location>();
        foreach (var heatPipe in heatPipes)
        {
            queue.Enqueue(heatPipe);
        }

        Span<Location> adjacent = stackalloc Location[4];
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (destinations.Contains(current))
            {
                var path = new List<Location>();
                for (var location = current; !heatPipes.Contains(location); location = previous[location])
                {
                    path.Add(location);
                }

                return path;
            }

            grid.GetAdjacent(adjacent, current);
            for (var i = 0; i < adjacent.Length; i++)
            {
                var next = adjacent[i];
                if (!next.IsValid || !visited.Add(next) || (!grid.IsEmpty(next) && !heatPipes.Contains(next)))
                {
                    continue;
                }

                previous.Add(next, current);
                queue.Enqueue(next);
            }
        }

        return null;
    }
}
