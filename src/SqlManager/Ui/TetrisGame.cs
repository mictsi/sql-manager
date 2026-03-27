using System.Drawing;
using System.Text;

namespace SqlManager;

internal enum TetrisPieceType
{
    I,
    O,
    T,
    L,
    J,
    S,
    Z
}

internal sealed class TetrisGame
{
    private static readonly IReadOnlyDictionary<TetrisPieceType, Point[][]> PieceRotations = new Dictionary<TetrisPieceType, Point[][]>
    {
        [TetrisPieceType.I] =
        [
            [new Point(0, 1), new Point(1, 1), new Point(2, 1), new Point(3, 1)],
            [new Point(2, 0), new Point(2, 1), new Point(2, 2), new Point(2, 3)],
            [new Point(0, 2), new Point(1, 2), new Point(2, 2), new Point(3, 2)],
            [new Point(1, 0), new Point(1, 1), new Point(1, 2), new Point(1, 3)]
        ],
        [TetrisPieceType.O] =
        [
            [new Point(1, 0), new Point(2, 0), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(2, 0), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(2, 0), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(2, 0), new Point(1, 1), new Point(2, 1)]
        ],
        [TetrisPieceType.T] =
        [
            [new Point(1, 0), new Point(0, 1), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(1, 1), new Point(2, 1), new Point(1, 2)],
            [new Point(0, 1), new Point(1, 1), new Point(2, 1), new Point(1, 2)],
            [new Point(1, 0), new Point(0, 1), new Point(1, 1), new Point(1, 2)]
        ],
        [TetrisPieceType.L] =
        [
            [new Point(2, 0), new Point(0, 1), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(1, 1), new Point(1, 2), new Point(2, 2)],
            [new Point(0, 1), new Point(1, 1), new Point(2, 1), new Point(0, 2)],
            [new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(1, 2)]
        ],
        [TetrisPieceType.J] =
        [
            [new Point(0, 0), new Point(0, 1), new Point(1, 1), new Point(2, 1)],
            [new Point(1, 0), new Point(2, 0), new Point(1, 1), new Point(1, 2)],
            [new Point(0, 1), new Point(1, 1), new Point(2, 1), new Point(2, 2)],
            [new Point(1, 0), new Point(1, 1), new Point(0, 2), new Point(1, 2)]
        ],
        [TetrisPieceType.S] =
        [
            [new Point(1, 0), new Point(2, 0), new Point(0, 1), new Point(1, 1)],
            [new Point(1, 0), new Point(1, 1), new Point(2, 1), new Point(2, 2)],
            [new Point(1, 1), new Point(2, 1), new Point(0, 2), new Point(1, 2)],
            [new Point(0, 0), new Point(0, 1), new Point(1, 1), new Point(1, 2)]
        ],
        [TetrisPieceType.Z] =
        [
            [new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(2, 1)],
            [new Point(2, 0), new Point(1, 1), new Point(2, 1), new Point(1, 2)],
            [new Point(0, 1), new Point(1, 1), new Point(1, 2), new Point(2, 2)],
            [new Point(1, 0), new Point(0, 1), new Point(1, 1), new Point(0, 2)]
        ]
    };

    private readonly Random _random;
    private readonly bool[,] _board;
    private TetrisPieceType _activePieceType;
    private int _rotationIndex;
    private Point _origin;

    public TetrisGame(int width = 10, int height = 18, Random? random = null)
    {
        if (width < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be at least 8.");
        }

        if (height < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be at least 10.");
        }

        Width = width;
        Height = height;
        _random = random ?? Random.Shared;
        _board = new bool[width, height];
        Reset();
    }

    public int Width { get; }

    public int Height { get; }

    public int Score { get; private set; }

    public int LinesCleared { get; private set; }

    public bool IsGameOver { get; private set; }

    public IReadOnlyList<Point> ActiveCells => GetTranslatedCells(_activePieceType, _rotationIndex, _origin);

    public void Reset()
    {
        Array.Clear(_board);
        Score = 0;
        LinesCleared = 0;
        IsGameOver = false;
        SpawnPiece();
    }

    public void Tick()
    {
        if (IsGameOver)
        {
            return;
        }

        if (!TryMove(0, 1))
        {
            LockPiece();
        }
    }

    public void MoveLeft() => TryMove(-1, 0);

    public void MoveRight() => TryMove(1, 0);

    public void SoftDrop()
    {
        if (!TryMove(0, 1))
        {
            LockPiece();
        }
    }

    public void HardDrop()
    {
        if (IsGameOver)
        {
            return;
        }

        while (TryMove(0, 1))
        {
        }

        LockPiece();
    }

    public void RotateClockwise()
    {
        if (IsGameOver)
        {
            return;
        }

        var nextRotation = (_rotationIndex + 1) % 4;
        if (CanPlace(_activePieceType, nextRotation, _origin))
        {
            _rotationIndex = nextRotation;
            return;
        }

        foreach (var horizontalKick in new[] { -1, 1, -2, 2 })
        {
            var kickedOrigin = new Point(_origin.X + horizontalKick, _origin.Y);
            if (CanPlace(_activePieceType, nextRotation, kickedOrigin))
            {
                _origin = kickedOrigin;
                _rotationIndex = nextRotation;
                return;
            }
        }
    }

    public string RenderBoard()
    {
        var activeCells = ActiveCells;
        var builder = new StringBuilder();
        builder.Append('+').Append(new string('-', Width)).AppendLine("+");

        for (var y = 0; y < Height; y++)
        {
            builder.Append('|');
            for (var x = 0; x < Width; x++)
            {
                var point = new Point(x, y);
                if (activeCells.Any(cell => cell.Equals(point)))
                {
                    builder.Append('@');
                }
                else
                {
                    builder.Append(_board[x, y] ? '#' : ' ');
                }
            }

            builder.AppendLine("|");
        }

        builder.Append('+').Append(new string('-', Width)).Append('+');
        return builder.ToString();
    }

    internal void SetCell(int x, int y, bool occupied)
        => _board[x, y] = occupied;

    internal bool IsOccupied(int x, int y)
        => _board[x, y];

    internal void SetActivePieceForTest(TetrisPieceType pieceType, int rotationIndex, int originX, int originY)
    {
        _activePieceType = pieceType;
        _rotationIndex = ((rotationIndex % 4) + 4) % 4;
        _origin = new Point(originX, originY);
        IsGameOver = false;
    }

    private void SpawnPiece()
    {
        _activePieceType = Enum.GetValues<TetrisPieceType>()[_random.Next(Enum.GetValues<TetrisPieceType>().Length)];
        _rotationIndex = 0;
        _origin = new Point((Width / 2) - 2, 0);

        if (!CanPlace(_activePieceType, _rotationIndex, _origin))
        {
            IsGameOver = true;
        }
    }

    private bool TryMove(int dx, int dy)
    {
        var nextOrigin = new Point(_origin.X + dx, _origin.Y + dy);
        if (!CanPlace(_activePieceType, _rotationIndex, nextOrigin))
        {
            return false;
        }

        _origin = nextOrigin;
        return true;
    }

    private bool CanPlace(TetrisPieceType pieceType, int rotationIndex, Point origin)
    {
        foreach (var cell in GetTranslatedCells(pieceType, rotationIndex, origin))
        {
            if (cell.X < 0 || cell.X >= Width || cell.Y < 0 || cell.Y >= Height)
            {
                return false;
            }

            if (_board[cell.X, cell.Y])
            {
                return false;
            }
        }

        return true;
    }

    private List<Point> GetTranslatedCells(TetrisPieceType pieceType, int rotationIndex, Point origin)
        => PieceRotations[pieceType][rotationIndex]
            .Select(point => new Point(point.X + origin.X, point.Y + origin.Y))
            .ToList();

    private void LockPiece()
    {
        foreach (var cell in ActiveCells)
        {
            if (cell.X < 0 || cell.X >= Width || cell.Y < 0 || cell.Y >= Height)
            {
                IsGameOver = true;
                return;
            }

            _board[cell.X, cell.Y] = true;
        }

        var cleared = ClearLines();
        LinesCleared += cleared;
        Score += cleared switch
        {
            1 => 100,
            2 => 250,
            3 => 450,
            4 => 800,
            _ => 0
        };

        SpawnPiece();
    }

    private int ClearLines()
    {
        var cleared = 0;
        for (var y = Height - 1; y >= 0; y--)
        {
            var full = true;
            for (var x = 0; x < Width; x++)
            {
                if (!_board[x, y])
                {
                    full = false;
                    break;
                }
            }

            if (!full)
            {
                continue;
            }

            cleared++;
            for (var row = y; row > 0; row--)
            {
                for (var x = 0; x < Width; x++)
                {
                    _board[x, row] = _board[x, row - 1];
                }
            }

            for (var x = 0; x < Width; x++)
            {
                _board[x, 0] = false;
            }

            y++;
        }

        return cleared;
    }
}