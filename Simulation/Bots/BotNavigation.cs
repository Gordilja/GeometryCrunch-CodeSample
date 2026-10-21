namespace Quantum
{
    using System.Collections.Generic;
    using Photon.Deterministic;

    /// Grid pathfinding for bots around static walls (cover, arena edges).
    ///
    /// The walkability grid is built once per map and bot radius from the map's static colliders and cached. It only
    /// depends on baked map data, so every client builds the same grid and the cache never needs rollback.
    /// Movement goes straight to the goal while that path is clear; otherwise A* (8-way, integer costs) finds a route
    /// and the bot heads for the furthest path point it can reach in a straight line (string pulling).
    public static unsafe class BotNavigation
    {
        static readonly FP CellSize = FP._0_50;
        static readonly FP RepathInterval = FP._0_50;
        static readonly FP WaypointReachedSqr = FP._0_50 * FP._0_50;
        const int MaxLookahead = 16;
        const int MaxSearchNodes = 6000;

        sealed class NavGrid
        {
            public int Width, Height;
            public FPVector2 Origin;      // world position of cell (0,0)'s centre
            public FP Clearance;
            public bool[] Blocked;
        }

        static readonly Dictionary<long, NavGrid> Grids = new Dictionary<long, NavGrid>();
        static readonly object Lock = new object();

        // A* buffers, reused (simulation runs one session at a time per thread; access is locked anyway)
        static int[] _gCost = new int[0], _parent = new int[0], _heap = new int[0], _heapPos = new int[0];
        static byte[] _state = new byte[0]; // 0 unseen, 1 open, 2 closed
        static readonly List<int> _path = new List<int>(128);

        /// Direction for this tick toward goal, going around walls. Use in place of BotQueries.DirectionTo.
        public static FPVector2 SteerTowards(Frame f, EntityRef entity, FPVector2 goal)
        {
            var bot = f.Unsafe.GetPointer<Bot>(entity);
            var position = f.Get<Transform2D>(entity).Position;
            var clearance = BotClearance(f, entity);

            bot->NavTimer -= f.DeltaTime;

            // Clear straight line: no path needed
            if (IsClear(f, position, goal, clearance))
            {
                bot->HasNavWaypoint = false;
                return BotQueries.DirectionTo(position, goal);
            }

            bool goalMoved = FPVector2.DistanceSquared(goal, bot->NavGoal) > FP._2 * FP._2;
            bool reached = bot->HasNavWaypoint && FPVector2.DistanceSquared(position, bot->NavWaypoint) < WaypointReachedSqr;
            if (!bot->HasNavWaypoint || goalMoved || reached || bot->NavTimer <= FP._0)
            {
                bot->NavGoal = goal;
                bot->NavTimer = RepathInterval;
                bot->HasNavWaypoint = TryFindWaypoint(f, position, goal, clearance, out var waypoint);
                bot->NavWaypoint = waypoint;
            }

            return bot->HasNavWaypoint
                ? BotQueries.DirectionTo(position, bot->NavWaypoint)
                : BotQueries.DirectionTo(position, goal);
        }

        static FP BotClearance(Frame f, EntityRef entity)
        {
            if (f.TryGet<TopDownKCC>(entity, out var kcc))
            {
                var settings = f.FindAsset<TopDownKCCSettings>(kcc.Settings.Id);
                if (settings != null)
                    return settings.Radius;
            }
            return FP._1;
        }

        /// True when a circle the bot's size can travel from a to b without touching a static collider.
        public static bool IsClear(Frame f, FPVector2 from, FPVector2 to, FP clearance)
        {
            var delta = to - from;
            if (delta.SqrMagnitude < FP._0_01)
                return true;

            var hits = f.Physics2D.ShapeCastAll(from, FP._0, Shape2D.CreateCircle(clearance * FP._0_75), delta,
                -1, QueryOptions.HitStatics);
            return hits.Count == 0;
        }

        static bool TryFindWaypoint(Frame f, FPVector2 position, FPVector2 goal, FP clearance, out FPVector2 waypoint)
        {
            waypoint = goal;
            lock (Lock)
            {
                var grid = GetGrid(f, clearance);
                if (grid == null)
                    return false;

                int start = NearestFree(grid, CellOf(grid, position));
                int end = NearestFree(grid, CellOf(grid, goal));
                if (start < 0 || end < 0 || !FindPath(grid, start, end))
                    return false;

                // _path runs start → end. Head for the furthest point (within the lookahead) reachable in a straight line.
                int last = _path.Count - 1;
                int limit = last < MaxLookahead ? last : MaxLookahead;
                for (int i = limit; i >= 1; i--)
                {
                    var point = CellCenter(grid, _path[i]);
                    if (i == 1 || IsClear(f, position, point, clearance))
                    {
                        waypoint = i == last ? goal : point;
                        return true;
                    }
                }

                waypoint = last >= 1 ? CellCenter(grid, _path[1]) : goal;
                return true;
            }
        }

        // ── Grid ────────────────────────────────────────────────────────────────

        static NavGrid GetGrid(Frame f, FP clearance)
        {
            var map = f.Map;
            if (map == null)
                return null;

            long key = (long)map.Guid.Value * 397 ^ clearance.RawValue;
            if (Grids.TryGetValue(key, out var cached))
                return cached;

            var grid = BuildGrid(f, map, clearance);
            Grids[key] = grid;
            return grid;
        }

        static NavGrid BuildGrid(Frame f, Map map, FP clearance)
        {
            // Bounds = everything the walls cover, plus a margin (works for any map without configuration)
            FP minX = FP._0, minY = FP._0, maxX = FP._0, maxY = FP._0;
            bool any = false;
            var colliders = map.StaticColliders2D;
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    FP extent = c.ShapeType == Shape2DType.Box ? c.BoxExtents.Magnitude
                              : c.ShapeType == Shape2DType.Circle ? c.CircleRadius
                              : FP._2;
                    var p = c.Position;
                    if (!any) { minX = p.X - extent; maxX = p.X + extent; minY = p.Y - extent; maxY = p.Y + extent; any = true; }
                    else
                    {
                        minX = FPMath.Min(minX, p.X - extent); maxX = FPMath.Max(maxX, p.X + extent);
                        minY = FPMath.Min(minY, p.Y - extent); maxY = FPMath.Max(maxY, p.Y + extent);
                    }
                }
            }

            if (!any) { minX = minY = -FP._10 * 2; maxX = maxY = FP._10 * 2; }

            // Keep the grid a sane size even on huge maps
            var limit = FP._10 * 8;
            minX = FPMath.Max(minX - FP._2, -limit); minY = FPMath.Max(minY - FP._2, -limit);
            maxX = FPMath.Min(maxX + FP._2, limit); maxY = FPMath.Min(maxY + FP._2, limit);

            var grid = new NavGrid
            {
                Width = FPMath.CeilToInt((maxX - minX) / CellSize) + 1,
                Height = FPMath.CeilToInt((maxY - minY) / CellSize) + 1,
                Origin = new FPVector2(minX, minY),
                Clearance = clearance
            };
            grid.Blocked = new bool[grid.Width * grid.Height];

            var shape = Shape2D.CreateCircle(clearance * FP._0_75);
            for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                var hits = f.Physics2D.OverlapShape(CellCenter(grid, y * grid.Width + x), FP._0, shape, -1, options: QueryOptions.HitStatics);
                grid.Blocked[y * grid.Width + x] = hits.Count > 0;
            }

            return grid;
        }

        static int CellOf(NavGrid g, FPVector2 world)
        {
            int x = FPMath.RoundToInt((world.X - g.Origin.X) / CellSize);
            int y = FPMath.RoundToInt((world.Y - g.Origin.Y) / CellSize);
            if (x < 0) x = 0; else if (x >= g.Width) x = g.Width - 1;
            if (y < 0) y = 0; else if (y >= g.Height) y = g.Height - 1;
            return y * g.Width + x;
        }

        static FPVector2 CellCenter(NavGrid g, int cell)
        {
            int x = cell % g.Width, y = cell / g.Width;
            return g.Origin + new FPVector2(x * CellSize, y * CellSize);
        }

        // Closest walkable cell (rings outward), for start/goal points inside or hugging a wall
        static int NearestFree(NavGrid g, int cell)
        {
            if (!g.Blocked[cell]) return cell;
            int cx = cell % g.Width, cy = cell / g.Width;
            for (int r = 1; r <= 8; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= g.Width || y >= g.Height) continue;
                int c = y * g.Width + x;
                if (!g.Blocked[c]) return c;
            }
            return -1;
        }

        // ── A* (8-way, integer costs 10 / 14, octile heuristic, no corner cutting) ───

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        static bool FindPath(NavGrid g, int start, int end)
        {
            _path.Clear();
            int n = g.Width * g.Height;
            if (_gCost.Length < n)
            {
                _gCost = new int[n]; _parent = new int[n]; _heap = new int[n]; _heapPos = new int[n]; _state = new byte[n];
            }
            System.Array.Clear(_state, 0, n);

            int heapCount = 0;
            _gCost[start] = 0; _parent[start] = -1; _state[start] = 1;
            Push(start, ref heapCount, g, end);

            int expanded = 0;
            while (heapCount > 0)
            {
                int current = Pop(ref heapCount, g, end);
                if (current == end)
                {
                    for (int c = end; c != -1; c = _parent[c]) _path.Add(c);
                    _path.Reverse();
                    return true;
                }

                _state[current] = 2;
                if (++expanded > MaxSearchNodes) return false;

                int cx = current % g.Width, cy = current / g.Width;
                for (int i = 0; i < 8; i++)
                {
                    int nx = cx + DX[i], ny = cy + DY[i];
                    if (nx < 0 || ny < 0 || nx >= g.Width || ny >= g.Height) continue;
                    int next = ny * g.Width + nx;
                    if (g.Blocked[next] || _state[next] == 2) continue;

                    // Diagonal: both side cells must be free (no squeezing past wall corners)
                    if (i >= 4 && (g.Blocked[cy * g.Width + nx] || g.Blocked[ny * g.Width + cx])) continue;

                    int cost = _gCost[current] + (i >= 4 ? 14 : 10);
                    if (_state[next] == 1)
                    {
                        if (cost >= _gCost[next]) continue;
                        _gCost[next] = cost; _parent[next] = current;
                        SiftUp(_heapPos[next], g, end);
                    }
                    else
                    {
                        _gCost[next] = cost; _parent[next] = current; _state[next] = 1;
                        Push(next, ref heapCount, g, end);
                    }
                }
            }

            return false;
        }

        static int F(int cell, NavGrid g, int end)
        {
            int dx = System.Math.Abs(cell % g.Width - end % g.Width);
            int dy = System.Math.Abs(cell / g.Width - end / g.Width);
            int h = dx < dy ? 14 * dx + 10 * (dy - dx) : 14 * dy + 10 * (dx - dy);
            return _gCost[cell] + h;
        }

        // Ties broken by cell index so the search order is identical on every client
        static bool Less(int a, int b, NavGrid g, int end)
        {
            int fa = F(a, g, end), fb = F(b, g, end);
            return fa < fb || (fa == fb && a < b);
        }

        static void Push(int cell, ref int count, NavGrid g, int end)
        {
            _heap[count] = cell; _heapPos[cell] = count; count++;
            SiftUp(count - 1, g, end);
        }

        static int Pop(ref int count, NavGrid g, int end)
        {
            int top = _heap[0];
            count--;
            if (count > 0)
            {
                _heap[0] = _heap[count]; _heapPos[_heap[0]] = 0;
                SiftDown(0, count, g, end);
            }
            return top;
        }

        static void SiftUp(int i, NavGrid g, int end)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Less(_heap[i], _heap[parent], g, end)) break;
                Swap(i, parent); i = parent;
            }
        }

        static void SiftDown(int i, int count, NavGrid g, int end)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, best = i;
                if (l < count && Less(_heap[l], _heap[best], g, end)) best = l;
                if (r < count && Less(_heap[r], _heap[best], g, end)) best = r;
                if (best == i) return;
                Swap(i, best); i = best;
            }
        }

        static void Swap(int a, int b)
        {
            int t = _heap[a]; _heap[a] = _heap[b]; _heap[b] = t;
            _heapPos[_heap[a]] = a; _heapPos[_heap[b]] = b;
        }
    }
}
