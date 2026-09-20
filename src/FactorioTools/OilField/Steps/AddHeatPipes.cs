using System;
using System.Collections.Generic;
using System.Linq;

namespace Knapcode.FactorioTools.OilField;

/// <summary>
/// Adds one orthogonally connected heat-pipe network. A heat pipe one tile away, including diagonally, keeps an
/// Aquilo entity warm. The network is deliberately left exposed so it can be connected to a heating tower or reactor.
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
        var targets = GetTargets(context.Grid);
        var heatPipes = new HashSet<Location>();

        foreach (var target in targets)
        {
            if (IsHeated(target, heatPipes))
            {
                continue;
            }

            var candidates = GetCandidates(context.Grid, target);
            if (candidates.Count == 0)
            {
                throw new FactorioToolsException("No valid heat-pipe placement could be found for an Aquilo entity.", badInput: true);
            }

            if (heatPipes.Count == 0)
            {
                heatPipes.Add(candidates[0]);
                continue;
            }

            var path = FindShortestPath(context.Grid, heatPipes, candidates);
            if (path is null)
            {
                throw new FactorioToolsException("No route could be found to connect the Aquilo heat pipes.", badInput: true);
            }

            heatPipes.UnionWith(path);
        }

        foreach (var location in heatPipes)
        {
            context.Grid.AddEntity(location, new HeatPipe(context.Grid.GetId()));
        }

        if (context.Options.ValidateSolution)
        {
            foreach (var target in targets)
            {
                if (!IsHeated(target, heatPipes))
                {
                    throw new FactorioToolsException("An Aquilo entity is not adjacent to a heat pipe.");
                }
            }

            Validate.HeatPipesAreConnected(context);
        }
    }

    private static List<HeatTarget> GetTargets(SquareGrid grid)
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

        return idToTarget.Values
            .OrderBy(t => t.Locations.Min(l => l.Y))
            .ThenBy(t => t.Locations.Min(l => l.X))
            .ToList();
    }

    private static bool IsHeated(HeatTarget target, HashSet<Location> heatPipes)
    {
        foreach (var location in target.Locations)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (heatPipes.Contains(location.Translate(x, y)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static List<Location> GetCandidates(SquareGrid grid, HeatTarget target)
    {
        var candidates = new HashSet<Location>();
        foreach (var location in target.Locations)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    var candidate = location.Translate(x, y);
                    if (grid.IsInBounds(candidate) && grid.IsEmpty(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
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
