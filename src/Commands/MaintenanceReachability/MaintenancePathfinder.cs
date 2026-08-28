using System;
using System.Collections.Generic;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal struct GridCell : IEquatable<GridCell>
    {
        public readonly int X;
        public readonly int Y;

        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridCell other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is GridCell && Equals((GridCell)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
            }
        }

        public static bool operator ==(GridCell left, GridCell right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GridCell left, GridCell right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return X + "," + Y;
        }
    }

    internal sealed class MaintenanceGrid
    {
        private readonly bool[] _walkable;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public double CellSize { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }

        public MaintenanceGrid(
            int width,
            int height,
            double cellSize,
            double originX,
            double originY,
            bool defaultWalkable)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");
            if (cellSize <= 0.0 || double.IsNaN(cellSize) || double.IsInfinity(cellSize))
                throw new ArgumentOutOfRangeException("cellSize");
            if ((long)width * height > int.MaxValue)
                throw new ArgumentOutOfRangeException("width", "Grid is too large.");

            Width = width;
            Height = height;
            CellSize = cellSize;
            OriginX = originX;
            OriginY = originY;
            _walkable = new bool[width * height];
            FillWalkable(defaultWalkable);
        }

        public bool IsInside(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height;
        }

        public bool IsInside(GridCell cell)
        {
            return IsInside(cell.X, cell.Y);
        }

        public bool IsWalkable(int x, int y)
        {
            return IsInside(x, y) && _walkable[ToIndex(x, y)];
        }

        public bool IsWalkable(GridCell cell)
        {
            return IsWalkable(cell.X, cell.Y);
        }

        public void SetWalkable(int x, int y, bool walkable)
        {
            EnsureInside(x, y);
            _walkable[ToIndex(x, y)] = walkable;
        }

        public void SetWalkable(GridCell cell, bool walkable)
        {
            SetWalkable(cell.X, cell.Y, walkable);
        }

        public void SetBlocked(int x, int y)
        {
            SetWalkable(x, y, false);
        }

        public void SetBlocked(GridCell cell)
        {
            SetWalkable(cell, false);
        }

        public void FillWalkable(bool walkable)
        {
            for (int i = 0; i < _walkable.Length; i++) _walkable[i] = walkable;
        }

        public GridCell WorldToCell(double worldX, double worldY)
        {
            return new GridCell(
                (int)Math.Floor((worldX - OriginX) / CellSize),
                (int)Math.Floor((worldY - OriginY) / CellSize));
        }

        public bool TryWorldToCell(double worldX, double worldY, out GridCell cell)
        {
            cell = WorldToCell(worldX, worldY);
            return IsInside(cell);
        }

        public MaintenancePoint2 CellCenter(GridCell cell)
        {
            EnsureInside(cell.X, cell.Y);
            return new MaintenancePoint2(
                OriginX + (cell.X + 0.5) * CellSize,
                OriginY + (cell.Y + 0.5) * CellSize);
        }

        public MaintenanceGrid Clone()
        {
            var clone = new MaintenanceGrid(Width, Height, CellSize, OriginX, OriginY, false);
            Array.Copy(_walkable, clone._walkable, _walkable.Length);
            return clone;
        }

        internal int ToIndex(int x, int y)
        {
            return y * Width + x;
        }

        internal GridCell FromIndex(int index)
        {
            return new GridCell(index % Width, index / Width);
        }

        private void EnsureInside(int x, int y)
        {
            if (!IsInside(x, y)) throw new ArgumentOutOfRangeException("x", "Cell is outside the grid.");
        }
    }

    internal static class MaintenancePathfinder
    {
        private static readonly int[] NeighborDx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] NeighborDy = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private const int OrthogonalCost = 10;
        private const int DiagonalCost = 14;

        public static List<GridCell> FindPath8(MaintenanceGrid grid, GridCell start, GridCell goal)
        {
            List<GridCell> path;
            double cost;
            return TryFindPath8(grid, start, goal, out path, out cost)
                ? path
                : new List<GridCell>();
        }

        public static bool TryFindPath8(
            MaintenanceGrid grid,
            GridCell start,
            GridCell goal,
            out List<GridCell> path,
            out double cost)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            path = new List<GridCell>();
            cost = double.PositiveInfinity;
            if (!grid.IsWalkable(start) || !grid.IsWalkable(goal)) return false;
            if (start == goal)
            {
                path.Add(start);
                cost = 0.0;
                return true;
            }

            int count = checked(grid.Width * grid.Height);
            int startIndex = grid.ToIndex(start.X, start.Y);
            int goalIndex = grid.ToIndex(goal.X, goal.Y);
            var gScore = new int[count];
            var cameFrom = new int[count];
            var closed = new bool[count];
            for (int i = 0; i < count; i++)
            {
                gScore[i] = int.MaxValue;
                cameFrom[i] = -1;
            }

            var open = new MinHeap();
            gScore[startIndex] = 0;
            open.Push(new HeapNode(startIndex, OctileHeuristic(start, goal), 0));

            while (open.Count > 0)
            {
                HeapNode node = open.Pop();
                int currentIndex = node.Index;
                if (closed[currentIndex]) continue;
                if (node.GScore != gScore[currentIndex]) continue;
                if (currentIndex == goalIndex)
                {
                    path = ReconstructPath(grid, cameFrom, currentIndex);
                    cost = CalculatePathLength(grid, path);
                    return true;
                }

                closed[currentIndex] = true;
                GridCell current = grid.FromIndex(currentIndex);
                for (int n = 0; n < NeighborDx.Length; n++)
                {
                    int dx = NeighborDx[n];
                    int dy = NeighborDy[n];
                    int nx = current.X + dx;
                    int ny = current.Y + dy;
                    if (!CanStep(grid, current.X, current.Y, nx, ny)) continue;

                    int neighborIndex = grid.ToIndex(nx, ny);
                    if (closed[neighborIndex]) continue;
                    int stepCost = dx != 0 && dy != 0 ? DiagonalCost : OrthogonalCost;
                    int tentative = gScore[currentIndex] + stepCost;
                    if (tentative >= gScore[neighborIndex]) continue;

                    gScore[neighborIndex] = tentative;
                    cameFrom[neighborIndex] = currentIndex;
                    int fScore = tentative + OctileHeuristic(new GridCell(nx, ny), goal);
                    open.Push(new HeapNode(neighborIndex, fScore, tentative));
                }
            }

            return false;
        }

        public static List<List<GridCell>> GetConnectedComponents(MaintenanceGrid grid)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            int componentCount;
            int[] labels = BuildComponentLabels(grid, out componentCount);
            var components = new List<List<GridCell>>(componentCount);
            for (int i = 0; i < componentCount; i++) components.Add(new List<GridCell>());
            for (int index = 0; index < labels.Length; index++)
            {
                int label = labels[index];
                if (label >= 0) components[label].Add(grid.FromIndex(index));
            }
            return components;
        }

        public static int[] BuildComponentLabels(MaintenanceGrid grid, out int componentCount)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            int count = checked(grid.Width * grid.Height);
            var labels = new int[count];
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;
            componentCount = 0;
            var queue = new Queue<int>();

            for (int seedIndex = 0; seedIndex < count; seedIndex++)
            {
                GridCell seed = grid.FromIndex(seedIndex);
                if (labels[seedIndex] >= 0 || !grid.IsWalkable(seed)) continue;
                int label = componentCount++;
                labels[seedIndex] = label;
                queue.Enqueue(seedIndex);

                while (queue.Count > 0)
                {
                    int currentIndex = queue.Dequeue();
                    GridCell current = grid.FromIndex(currentIndex);
                    for (int n = 0; n < NeighborDx.Length; n++)
                    {
                        int nx = current.X + NeighborDx[n];
                        int ny = current.Y + NeighborDy[n];
                        if (!CanStep(grid, current.X, current.Y, nx, ny)) continue;
                        int nextIndex = grid.ToIndex(nx, ny);
                        if (labels[nextIndex] >= 0) continue;
                        labels[nextIndex] = label;
                        queue.Enqueue(nextIndex);
                    }
                }
            }

            return labels;
        }

        public static List<GridCell> SimplifyPath(MaintenanceGrid grid, IList<GridCell> path)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            if (path == null) throw new ArgumentNullException("path");
            var result = new List<GridCell>();
            if (path.Count == 0) return result;
            if (path.Count == 1)
            {
                result.Add(path[0]);
                return result;
            }

            int anchor = 0;
            result.Add(path[0]);
            while (anchor < path.Count - 1)
            {
                int furthest = anchor + 1;
                for (int candidate = path.Count - 1; candidate > anchor + 1; candidate--)
                {
                    if (!HasLineOfSight(grid, path[anchor], path[candidate])) continue;
                    furthest = candidate;
                    break;
                }
                result.Add(path[furthest]);
                anchor = furthest;
            }
            return result;
        }

        public static double CalculatePathLength(MaintenanceGrid grid, IList<GridCell> path)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            if (path == null) throw new ArgumentNullException("path");
            double length = 0.0;
            for (int i = 1; i < path.Count; i++)
            {
                int dx = Math.Abs(path[i].X - path[i - 1].X);
                int dy = Math.Abs(path[i].Y - path[i - 1].Y);
                length += Math.Sqrt(dx * dx + dy * dy) * grid.CellSize;
            }
            return length;
        }

        public static bool HasLineOfSight(MaintenanceGrid grid, GridCell from, GridCell to)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            if (!grid.IsWalkable(from) || !grid.IsWalkable(to)) return false;

            int x = from.X;
            int y = from.Y;
            int dx = Math.Abs(to.X - from.X);
            int dy = Math.Abs(to.Y - from.Y);
            int sx = from.X < to.X ? 1 : -1;
            int sy = from.Y < to.Y ? 1 : -1;
            int error = dx - dy;

            while (x != to.X || y != to.Y)
            {
                int doubled = error * 2;
                int nextX = x;
                int nextY = y;
                if (doubled > -dy)
                {
                    error -= dy;
                    nextX += sx;
                }
                if (doubled < dx)
                {
                    error += dx;
                    nextY += sy;
                }
                if (!CanStep(grid, x, y, nextX, nextY)) return false;
                x = nextX;
                y = nextY;
            }
            return true;
        }

        public static bool FindNearestWalkable(
            MaintenanceGrid grid,
            GridCell seed,
            int maxRadius,
            out GridCell nearest)
        {
            if (grid == null) throw new ArgumentNullException("grid");
            if (maxRadius < 0) throw new ArgumentOutOfRangeException("maxRadius");
            nearest = seed;
            if (grid.IsWalkable(seed)) return true;

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minX = seed.X - radius;
                int maxX = seed.X + radius;
                int minY = seed.Y - radius;
                int maxY = seed.Y + radius;

                for (int x = minX; x <= maxX; x++)
                {
                    if (TryCandidate(grid, x, minY, ref nearest)) return true;
                    if (maxY != minY && TryCandidate(grid, x, maxY, ref nearest)) return true;
                }
                for (int y = minY + 1; y < maxY; y++)
                {
                    if (TryCandidate(grid, minX, y, ref nearest)) return true;
                    if (maxX != minX && TryCandidate(grid, maxX, y, ref nearest)) return true;
                }
            }
            return false;
        }

        private static bool TryCandidate(MaintenanceGrid grid, int x, int y, ref GridCell nearest)
        {
            if (!grid.IsWalkable(x, y)) return false;
            nearest = new GridCell(x, y);
            return true;
        }

        private static bool CanStep(MaintenanceGrid grid, int x, int y, int nextX, int nextY)
        {
            if (!grid.IsWalkable(nextX, nextY)) return false;
            int dx = nextX - x;
            int dy = nextY - y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0)) return false;
            if (dx != 0 && dy != 0)
            {
                // Both side-adjacent cells must be free. This forbids squeezing
                // diagonally through two obstacles that touch at a corner.
                return grid.IsWalkable(x + dx, y) && grid.IsWalkable(x, y + dy);
            }
            return true;
        }

        private static int OctileHeuristic(GridCell from, GridCell to)
        {
            int dx = Math.Abs(to.X - from.X);
            int dy = Math.Abs(to.Y - from.Y);
            int diagonal = Math.Min(dx, dy);
            int straight = Math.Max(dx, dy) - diagonal;
            return diagonal * DiagonalCost + straight * OrthogonalCost;
        }

        private static List<GridCell> ReconstructPath(
            MaintenanceGrid grid,
            int[] cameFrom,
            int currentIndex)
        {
            var reversed = new List<GridCell>();
            while (currentIndex >= 0)
            {
                reversed.Add(grid.FromIndex(currentIndex));
                currentIndex = cameFrom[currentIndex];
            }
            reversed.Reverse();
            return reversed;
        }

        private struct HeapNode
        {
            public readonly int Index;
            public readonly int FScore;
            public readonly int GScore;

            public HeapNode(int index, int fScore, int gScore)
            {
                Index = index;
                FScore = fScore;
                GScore = gScore;
            }
        }

        private sealed class MinHeap
        {
            private readonly List<HeapNode> _items = new List<HeapNode>();

            public int Count
            {
                get { return _items.Count; }
            }

            public void Push(HeapNode item)
            {
                _items.Add(item);
                int index = _items.Count - 1;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (!IsLess(_items[index], _items[parent])) break;
                    Swap(index, parent);
                    index = parent;
                }
            }

            public HeapNode Pop()
            {
                if (_items.Count == 0) throw new InvalidOperationException("Heap is empty.");
                HeapNode root = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                if (_items.Count == 0) return root;

                int index = 0;
                while (true)
                {
                    int left = index * 2 + 1;
                    int right = left + 1;
                    int smallest = index;
                    if (left < _items.Count && IsLess(_items[left], _items[smallest])) smallest = left;
                    if (right < _items.Count && IsLess(_items[right], _items[smallest])) smallest = right;
                    if (smallest == index) break;
                    Swap(index, smallest);
                    index = smallest;
                }
                return root;
            }

            private static bool IsLess(HeapNode left, HeapNode right)
            {
                if (left.FScore != right.FScore) return left.FScore < right.FScore;
                if (left.GScore != right.GScore) return left.GScore > right.GScore;
                return left.Index < right.Index;
            }

            private void Swap(int left, int right)
            {
                HeapNode temp = _items[left];
                _items[left] = _items[right];
                _items[right] = temp;
            }
        }
    }
}
