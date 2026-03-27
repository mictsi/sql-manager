using System.Drawing;
using System.Text;

namespace SqlManager;

internal enum SnakeDirection
{
    Up,
    Down,
    Left,
    Right
}

internal sealed class SnakeGame
{
    private readonly Random _random;
    private readonly List<Point> _segments = [];
    private SnakeDirection _direction;
    private SnakeDirection _pendingDirection;
    private Point _food;

    public SnakeGame(int width = 24, int height = 14, Random? random = null)
    {
        if (width < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be at least 8.");
        }

        if (height < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be at least 8.");
        }

        Width = width;
        Height = height;
        _random = random ?? Random.Shared;
        Reset();
    }

    public int Width { get; }

    public int Height { get; }

    public int Score { get; private set; }

    public bool IsGameOver { get; private set; }

    public IReadOnlyList<Point> Segments => _segments;

    public Point Food => _food;

    public SnakeDirection Direction => _direction;

    public void Reset()
    {
        Score = 0;
        IsGameOver = false;
        _segments.Clear();

        var centerX = Width / 2;
        var centerY = Height / 2;
        _segments.Add(new Point(centerX, centerY));
        _segments.Add(new Point(centerX - 1, centerY));
        _segments.Add(new Point(centerX - 2, centerY));

        _direction = SnakeDirection.Right;
        _pendingDirection = SnakeDirection.Right;
        SpawnFood();
    }

    public void ChangeDirection(SnakeDirection direction)
    {
        if (_segments.Count > 1 && IsOppositeDirection(_direction, direction))
        {
            return;
        }

        _pendingDirection = direction;
    }

    public void Tick()
    {
        if (IsGameOver)
        {
            return;
        }

        _direction = _pendingDirection;
        var nextHead = Translate(_segments[0], _direction);
        if (IsOutsideBoard(nextHead))
        {
            IsGameOver = true;
            return;
        }

        var willGrow = nextHead.Equals(_food);
        var collisionLength = willGrow ? _segments.Count : _segments.Count - 1;
        for (var index = 0; index < collisionLength; index++)
        {
            if (_segments[index].Equals(nextHead))
            {
                IsGameOver = true;
                return;
            }
        }

        _segments.Insert(0, nextHead);
        if (willGrow)
        {
            Score++;
            SpawnFood();
            return;
        }

        _segments.RemoveAt(_segments.Count - 1);
    }

    public string RenderBoard()
    {
        var builder = new StringBuilder();
        builder.Append('+').Append(new string('-', Width)).AppendLine("+");

        for (var y = 0; y < Height; y++)
        {
            builder.Append('|');
            for (var x = 0; x < Width; x++)
            {
                var current = new Point(x, y);
                builder.Append(GetCellRune(current));
            }

            builder.AppendLine("|");
        }

        builder.Append('+').Append(new string('-', Width)).Append('+');
        return builder.ToString();
    }

    private char GetCellRune(Point cell)
    {
        if (_segments[0].Equals(cell))
        {
            return '@';
        }

        if (_food.Equals(cell))
        {
            return '*';
        }

        for (var index = 1; index < _segments.Count; index++)
        {
            if (_segments[index].Equals(cell))
            {
                return 'o';
            }
        }

        return ' ';
    }

    private void SpawnFood()
    {
        var availableCells = new List<Point>();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var point = new Point(x, y);
                if (_segments.Any(segment => segment.Equals(point)))
                {
                    continue;
                }

                availableCells.Add(point);
            }
        }

        if (availableCells.Count == 0)
        {
            IsGameOver = true;
            _food = Point.Empty;
            return;
        }

        _food = availableCells[_random.Next(availableCells.Count)];
    }

    private bool IsOutsideBoard(Point point)
        => point.X < 0 || point.X >= Width || point.Y < 0 || point.Y >= Height;

    private static Point Translate(Point source, SnakeDirection direction)
        => direction switch
        {
            SnakeDirection.Up => new Point(source.X, source.Y - 1),
            SnakeDirection.Down => new Point(source.X, source.Y + 1),
            SnakeDirection.Left => new Point(source.X - 1, source.Y),
            _ => new Point(source.X + 1, source.Y)
        };

    private static bool IsOppositeDirection(SnakeDirection current, SnakeDirection next)
        => (current == SnakeDirection.Up && next == SnakeDirection.Down)
            || (current == SnakeDirection.Down && next == SnakeDirection.Up)
            || (current == SnakeDirection.Left && next == SnakeDirection.Right)
            || (current == SnakeDirection.Right && next == SnakeDirection.Left);
}