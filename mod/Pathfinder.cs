using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace MegabonkAI
{
    public enum SearchStatus
    {
        Idle,
        Searching,
        Succeeded,
        Failed
    }

    // A* over a walkability grid sampled lazily from world geometry and cached.
    //
    // Megabonk's maps are procedural, so there is no NavMesh to query. Each grid cell is probed
    // on first use: raycast down for ground, reject it if the surface is steeper than the player
    // can climb or if there is no body-room above it. Neighbours only connect when the height
    // change is something the player could walk, hop or drop - which is what forces the search
    // to route around a mountain rather than into its face.
    //
    // The search runs incrementally: a small slice of work per frame, resumed until it finds a
    // route. Routes across a large map need far more expansions than fit in one frame, and a
    // truncated search returns a path that dead-ends at a wall.
    public class Pathfinder
    {
        // Finer cells resolve sharp corners and narrow ramps that a coarse grid smeared over.
        // The incremental search absorbs the extra node count.
        public float Step = 2f;
        public int GroundMask;
        public int ObstacleMask;
        public float MaxSlope = 45f;
        public float PlayerRadius = 0.5f;
        // How much height a single cell step may gain. This must match what a walkable ramp
        // can deliver over one cell (Step * tan(MaxSlope)) - clamping it to a small "step"
        // height made every hill unclimbable. Vertical faces are rejected by the surface
        // normal and midpoint checks instead, which is what actually distinguishes a ramp
        // from a wall.
        public float MaxStepUp = 2.4f;
        public float MaxDrop = 5f;       // survivable drop

        public void ConfigureFromSlope()
        {
            // Stay a little under the theoretical maximum: a cell pair right at the slope limit
            // is exactly where the physics tends to stall the player halfway up.
            MaxStepUp = Mathf.Clamp(Step * Mathf.Tan(MaxSlope * Mathf.Deg2Rad) * 0.8f, 0.9f, 4f);
        }
        public float RayOriginY = 250f;
        public float RayLength = 700f;

        public int MaxExpansions = 30000;   // across the whole (multi-frame) search
        public float MaxTotalSeconds = 4f;

        public SearchStatus Status { get; private set; } = SearchStatus.Idle;
        public List<Vector3> ResultPath { get; } = new List<Vector3>();
        public bool ResultIsPartial { get; private set; }

        /// <summary>
        /// True when the search ran out of reachable cells before finding the goal - a definite
        /// "no route exists" rather than "ran out of time". Loot on an unreachable peak lands
        /// here, and the caller can drop it immediately instead of walking at it for seconds.
        /// </summary>
        public bool ExhaustedWithoutGoal { get; private set; }
        public int Expansions { get; private set; }
        public int CacheSize => _cells.Count;

        // Temporary instrumentation: explains why cells are rejected, so an over-strict
        // walkability rule shows itself instead of silently emptying the graph.
        public int ReachableCells = 0;
        public int DiagnosticsRemaining = 0;
        public string LastDiagnostic = "";

        private struct Cell
        {
            public bool Walkable;
            public float Height;
        }

        private readonly Dictionary<long, Cell> _cells = new Dictionary<long, Cell>();
        private readonly Dictionary<long, float> _blockedUntil = new Dictionary<long, float>();
        private readonly Dictionary<long, float> _gScore = new Dictionary<long, float>();
        private readonly Dictionary<long, long> _cameFrom = new Dictionary<long, long>();
        private readonly HashSet<long> _closed = new HashSet<long>();
        private readonly MinHeap _open = new MinHeap();
        private readonly Stopwatch _slice = new Stopwatch();

        private int _sx, _sz, _gx, _gz;
        private long _startKey, _goalKey;
        private long _bestKey;
        private float _bestH;
        private float _cellBudget;
        private float _searchStartedAt;

        public void ClearCache()
        {
            _cells.Clear();
            _midHeights.Clear();
            _edgePenalties.Clear();
            _blockedUntil.Clear();
        }

        public void Abort()
        {
            Status = SearchStatus.Idle;
            _open.Clear();
        }

        private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
        private static int KeyX(long k) => (int)(k >> 32);
        private static int KeyZ(long k) => (int)(uint)k;

        private int ToCell(float world) => Mathf.FloorToInt(world / Step);
        private float ToWorld(int cell) => (cell + 0.5f) * Step;

        /// <summary>
        /// Marks the area around a world position unusable for a while. Used when the bot
        /// proves in practice that it cannot traverse somewhere the geometry said was fine,
        /// so replans route around it instead of retrying the same spot forever.
        /// </summary>
        public void BlockAround(Vector3 world, float radius, float seconds)
        {
            int cx = ToCell(world.x);
            int cz = ToCell(world.z);
            int r = Mathf.Max(0, Mathf.CeilToInt(radius / Step));
            float until = Time.realtimeSinceStartup + seconds;

            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    _blockedUntil[Key(cx + dx, cz + dz)] = until;

            // walkability just changed here, so cached edge costs around it are stale
            _edgePenalties.Clear();
        }

        /// <summary>
        /// How many of the 3x3 cells around a world position are standable. Used to judge
        /// whether somewhere is open ground or jammed against a wall / off the map.
        /// </summary>
        public int OpennessAt(Vector3 world)
        {
            int cx = ToCell(world.x);
            int cz = ToCell(world.z);
            int open = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (Sample(cx + dx, cz + dz).Walkable) open++;
            return open;
        }

        private bool IsTemporarilyBlocked(long key)
        {
            if (!_blockedUntil.TryGetValue(key, out float until)) return false;
            if (Time.realtimeSinceStartup >= until)
            {
                _blockedUntil.Remove(key);
                return false;
            }
            return true;
        }

        private Cell Sample(int cx, int cz)
        {
            long key = Key(cx, cz);
            if (IsTemporarilyBlocked(key)) return new Cell { Walkable = false, Height = 0f };
            if (_cells.TryGetValue(key, out Cell cached)) return cached;

            var cell = new Cell { Walkable = false, Height = 0f };
            string why = "no ground hit";

            // A single ray down the centre of a cell is unreliable on blocky terrain: land it
            // near a block edge and it hits the *vertical face* instead of the flat top, so
            // perfectly good ground reads as a 90-degree cliff. That fragmented the graph into
            // small islands and made most targets look unreachable.
            //
            // Probe the centre first (the common case, one ray), and only if that fails, try
            // points spread across the cell footprint and take the flattest standable surface.
            float spread = Step * 0.3f;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                float ox = 0f, oz = 0f;
                switch (attempt)
                {
                    case 1: ox = spread; break;
                    case 2: ox = -spread; break;
                    case 3: oz = spread; break;
                    case 4: oz = -spread; break;
                }

                try
                {
                    Vector3 origin = new Vector3(ToWorld(cx) + ox, RayOriginY, ToWorld(cz) + oz);
                    if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, RayLength, GroundMask))
                        continue;

                    // remember a height even for unwalkable cells - neighbours compare against it
                    if (!cell.Walkable) cell.Height = hit.point.y;

                    float angle = Vector3.Angle(hit.normal, Vector3.up);
                    if (angle > MaxSlope) { why = $"slope {angle:0}deg"; continue; }

                    Vector3 body = hit.point + Vector3.up * (PlayerRadius + 0.6f);
                    if (Physics.CheckSphere(body, PlayerRadius * 0.55f, ObstacleMask))
                    {
                        why = "blocked overhead";
                        continue;
                    }

                    cell.Walkable = true;
                    cell.Height = hit.point.y;
                    break;
                }
                catch (Exception ex)
                {
                    why = "exception: " + ex.Message;
                    break;
                }
            }

            if (!cell.Walkable && DiagnosticsRemaining > 0)
            {
                DiagnosticsRemaining--;
                LastDiagnostic = $"cell({cx},{cz}) unwalkable: {why}";
            }

            _cells[key] = cell;
            return cell;
        }

        // Nearest standable cell, searched in rings - loot often sits just inside scenery so
        // the cell directly beneath it may not itself be walkable.
        private bool TryResolveCell(Vector3 world, int maxRing, out int outX, out int outZ)
        {
            int bx = ToCell(world.x);
            int bz = ToCell(world.z);

            if (Sample(bx, bz).Walkable) { outX = bx; outZ = bz; return true; }

            for (int ring = 1; ring <= maxRing; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring) continue;
                        if (Sample(bx + dx, bz + dz).Walkable)
                        {
                            outX = bx + dx;
                            outZ = bz + dz;
                            return true;
                        }
                    }
                }
            }

            outX = bx;
            outZ = bz;
            return false;
        }

        private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        private readonly Dictionary<long, float> _midHeights = new Dictionary<long, float>();
        private readonly Dictionary<long, float> _edgePenalties = new Dictionary<long, float>();

        /// <summary>
        /// Extra cost for cells next to unwalkable ground, so routes run down the middle of a
        /// ledge or corridor instead of scraping its edge.
        /// </summary>
        public float EdgeCostWeight = 1.6f;

        private float EdgePenalty(int cx, int cz)
        {
            long key = Key(cx, cz);
            if (_edgePenalties.TryGetValue(key, out float cached)) return cached;

            int blocked = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (!Sample(cx + dx, cz + dz).Walkable) blocked++;
                }

            float penalty = blocked * EdgeCostWeight;
            _edgePenalties[key] = penalty;
            return penalty;
        }

        // Samples the ground halfway between two cells and requires the climb to be gradual
        // on both halves - a 0.8m "step" spread over 2.5m is a ramp, the same 0.8m as a
        // vertical lip is a wall, and only this midpoint sample tells them apart.
        private bool MidpointTraversable(int ax, int az, float ah, int bx, int bz, float bh)
        {
            long key = Key(ax + bx, az + bz); // unique per unordered pair on the doubled grid

            if (!_midHeights.TryGetValue(key, out float midH))
            {
                float wx = (ToWorld(ax) + ToWorld(bx)) * 0.5f;
                float wz = (ToWorld(az) + ToWorld(bz)) * 0.5f;
                midH = float.NaN;

                // Same blocky-terrain caveat as Sample(): one ray can land on a vertical face
                // and reject a perfectly walkable transition, so probe a few nearby points
                // before calling it impassable.
                float jitter = Step * 0.25f;
                for (int attempt = 0; attempt < 5 && float.IsNaN(midH); attempt++)
                {
                    float ox = 0f, oz = 0f;
                    switch (attempt)
                    {
                        case 1: ox = jitter; break;
                        case 2: ox = -jitter; break;
                        case 3: oz = jitter; break;
                        case 4: oz = -jitter; break;
                    }

                    try
                    {
                        if (Physics.Raycast(new Vector3(wx + ox, RayOriginY, wz + oz), Vector3.down,
                                            out RaycastHit hit, RayLength, GroundMask))
                        {
                            if (Vector3.Angle(hit.normal, Vector3.up) <= MaxSlope)
                                midH = hit.point.y;
                        }
                    }
                    catch { break; }
                }

                _midHeights[key] = midH;
            }

            if (float.IsNaN(midH)) return false;

            // Each half-step covers half a cell, so it may only gain half a cell's worth of
            // climb. A gentle ramp passes; a lip hidden between two flat cells does not.
            float half = (Step * 0.5f) * Mathf.Tan(MaxSlope * Mathf.Deg2Rad) * 1.1f;
            return (midH - ah) <= half
                   && (midH - bh) <= half
                   && (ah - midH) <= MaxDrop
                   && (bh - midH) <= MaxDrop;
        }

        public void BeginSearch(Vector3 start, Vector3 goal)
        {
            ResultPath.Clear();
            ResultIsPartial = false;
            ExhaustedWithoutGoal = false;
            Expansions = 0;
            Status = SearchStatus.Failed;

            if (GroundMask == 0) return;
            if (!TryResolveCell(start, 3, out _sx, out _sz)) return;
            if (!TryResolveCell(goal, 5, out _gx, out _gz)) return;

            _startKey = Key(_sx, _sz);
            _goalKey = Key(_gx, _gz);
            if (_startKey == _goalKey) return; // already there, caller walks straight in

            float directCells = Mathf.Sqrt((_gx - _sx) * (_gx - _sx) + (_gz - _sz) * (_gz - _sz));
            _cellBudget = directCells * 3f + 30f;

            _gScore.Clear();
            _cameFrom.Clear();
            _closed.Clear();
            _open.Clear();

            _gScore[_startKey] = 0f;
            _bestH = Heuristic(_sx, _sz, _gx, _gz);
            _bestKey = _startKey;
            _open.Push(_startKey, _bestH);

            _searchStartedAt = Time.realtimeSinceStartup;
            Status = SearchStatus.Searching;
        }

        /// <summary>
        /// Advances the search for at most millisBudget, resuming where the last call stopped.
        /// Returns Searching while more work remains.
        /// </summary>
        public SearchStatus StepSearch(float millisBudget)
        {
            if (Status != SearchStatus.Searching) return Status;

            _slice.Restart();

            while (_open.Count > 0)
            {
                if (_slice.Elapsed.TotalMilliseconds >= millisBudget) { _slice.Stop(); return Status; }

                if (Expansions >= MaxExpansions ||
                    Time.realtimeSinceStartup - _searchStartedAt > MaxTotalSeconds)
                {
                    _slice.Stop();
                    return Finish(false);
                }

                long current = _open.Pop();
                if (_closed.Contains(current)) continue;
                _closed.Add(current);
                Expansions++;

                if (current == _goalKey)
                {
                    _bestKey = current;
                    _slice.Stop();
                    return Finish(true);
                }

                int cx = KeyX(current), cz = KeyZ(current);

                float h = Heuristic(cx, cz, _gx, _gz);
                if (h < _bestH) { _bestH = h; _bestKey = current; }

                float baseG = _gScore.TryGetValue(current, out float g) ? g : 0f;
                Cell currentCell = Sample(cx, cz);

                for (int i = 0; i < 8; i++)
                {
                    int nx = cx + DX[i], nz = cz + DZ[i];
                    long nKey = Key(nx, nz);
                    if (_closed.Contains(nKey)) continue;

                    if (Mathf.Sqrt((nx - _sx) * (nx - _sx) + (nz - _sz) * (nz - _sz)) > _cellBudget) continue;

                    Cell nCell = Sample(nx, nz);
                    if (!nCell.Walkable) continue;

                    float dh = nCell.Height - currentCell.Height;
                    if (dh > MaxStepUp) continue;   // wall / cliff face
                    if (dh < -MaxDrop) continue;    // lethal drop

                    // Cell centres can straddle a ridge or a step that neither endpoint shows.
                    // Check halfway when there's any real height change between them.
                    if (Mathf.Abs(dh) > 0.35f &&
                        !MidpointTraversable(cx, cz, currentCell.Height, nx, nz, nCell.Height))
                        continue;

                    bool diagonal = DX[i] != 0 && DZ[i] != 0;
                    if (diagonal)
                    {
                        if (!Sample(cx + DX[i], cz).Walkable) continue;
                        if (!Sample(cx, cz + DZ[i]).Walkable) continue;
                    }

                    // Hug the middle of walkable ground. Routes that skim the edge of a ridge
                    // or clip the inside of a corner are exactly where the player slides off
                    // or catches on geometry, so make proximity to a drop-off expensive.
                    float cost = (diagonal ? 1.41421f : 1f) * Step
                               + Mathf.Abs(dh) * 0.6f
                               + (dh < -1.5f ? 1.5f : 0f)
                               + EdgePenalty(nx, nz);

                    float tentative = baseG + cost;
                    if (_gScore.TryGetValue(nKey, out float known) && tentative >= known) continue;

                    _gScore[nKey] = tentative;
                    _cameFrom[nKey] = current;
                    _open.Push(nKey, tentative + Heuristic(nx, nz, _gx, _gz));
                }
            }

            _slice.Stop();
            ExhaustedWithoutGoal = true; // open set emptied: nowhere left to walk from here
            ReachableCells = _closed.Count;
            return Finish(false);
        }

        private SearchStatus Finish(bool reachedGoal)
        {
            ResultPath.Clear();
            ResultIsPartial = !reachedGoal;

            long endKey = reachedGoal ? _goalKey : _bestKey;

            // A partial result is only worth walking if it actually gets us closer.
            if (!reachedGoal)
            {
                float startH = Heuristic(_sx, _sz, _gx, _gz);
                if (endKey == _startKey || _bestH >= startH * 0.9f)
                {
                    Status = SearchStatus.Failed;
                    return Status;
                }
            }

            var reversed = new List<Vector3>();
            long node = endKey;
            int guard = 0;
            while (node != _startKey && guard++ < 8000)
            {
                int nx = KeyX(node), nz = KeyZ(node);
                Cell c = Sample(nx, nz);
                reversed.Add(new Vector3(ToWorld(nx), c.Height, ToWorld(nz)));
                if (!_cameFrom.TryGetValue(node, out node))
                {
                    Status = SearchStatus.Failed;
                    return Status;
                }
            }

            for (int i = reversed.Count - 1; i >= 0; i--) ResultPath.Add(reversed[i]);

            Status = ResultPath.Count > 0 ? SearchStatus.Succeeded : SearchStatus.Failed;
            return Status;
        }

        private float Heuristic(int ax, int az, int bx, int bz)
        {
            float dx = Mathf.Abs(ax - bx);
            float dz = Mathf.Abs(az - bz);
            return (Mathf.Max(dx, dz) + 0.41421f * Mathf.Min(dx, dz)) * Step;
        }

        // Small binary heap; System.Collections.Generic.PriorityQueue isn't on this target.
        private class MinHeap
        {
            private readonly List<long> _keys = new List<long>();
            private readonly List<float> _prio = new List<float>();

            public int Count => _keys.Count;

            public void Clear()
            {
                _keys.Clear();
                _prio.Clear();
            }

            public void Push(long key, float priority)
            {
                _keys.Add(key);
                _prio.Add(priority);
                int i = _keys.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_prio[parent] <= _prio[i]) break;
                    Swap(parent, i);
                    i = parent;
                }
            }

            public long Pop()
            {
                long top = _keys[0];
                int last = _keys.Count - 1;
                _keys[0] = _keys[last];
                _prio[0] = _prio[last];
                _keys.RemoveAt(last);
                _prio.RemoveAt(last);

                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, smallest = i;
                    if (l < _keys.Count && _prio[l] < _prio[smallest]) smallest = l;
                    if (r < _keys.Count && _prio[r] < _prio[smallest]) smallest = r;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
                return top;
            }

            private void Swap(int a, int b)
            {
                (_keys[a], _keys[b]) = (_keys[b], _keys[a]);
                (_prio[a], _prio[b]) = (_prio[b], _prio[a]);
            }
        }
    }
}
