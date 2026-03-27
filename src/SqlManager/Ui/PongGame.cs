using System.Drawing;
using System.Text;

namespace SqlManager;

internal sealed class PongGame
{
    public PongGame(int width = 28, int height = 14, int paddleSize = 4)
    {
        if (width < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be at least 16.");
        }

        if (height < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be at least 8.");
        }

        if (paddleSize < 2 || paddleSize >= height)
        {
            throw new ArgumentOutOfRangeException(nameof(paddleSize), "Paddle size must fit within the board.");
        }

        Width = width;
        Height = height;
        PaddleSize = paddleSize;
        Reset();
    }

    public int Width { get; }

    public int Height { get; }

    public int PaddleSize { get; }

    public int Score { get; private set; }

    public bool IsGameOver { get; private set; }

    public int PlayerPaddleTop { get; private set; }

    public int CpuPaddleTop { get; private set; }

    public Point Ball { get; private set; }

    public void Reset()
    {
        Score = 0;
        IsGameOver = false;
        PlayerPaddleTop = (Height - PaddleSize) / 2;
        CpuPaddleTop = (Height - PaddleSize) / 2;
        ResetBall(-1, -1);
    }

    public void MovePlayerUp() => MovePlayer(-1);

    public void MovePlayerDown() => MovePlayer(1);

    public void Tick()
    {
        if (IsGameOver)
        {
            return;
        }

        MoveCpu();

        var nextX = Ball.X + _velocityX;
        var nextY = Ball.Y + _velocityY;

        if (nextY < 0 || nextY >= Height)
        {
            _velocityY *= -1;
            nextY = Ball.Y + _velocityY;
        }

        if (_velocityX < 0 && nextX <= 1)
        {
            if (IsWithinPaddle(PlayerPaddleTop, nextY))
            {
                Score++;
                _velocityX = 1;
                _velocityY = CalculateBounceVelocity(nextY, PlayerPaddleTop);
                nextX = 1;
                nextY = Math.Clamp(Ball.Y + _velocityY, 0, Height - 1);
            }
            else if (nextX < 1)
            {
                Ball = new Point(0, Math.Clamp(nextY, 0, Height - 1));
                IsGameOver = true;
                return;
            }
        }

        if (_velocityX > 0 && nextX >= Width - 2)
        {
            if (IsWithinPaddle(CpuPaddleTop, nextY))
            {
                _velocityX = -1;
                _velocityY = CalculateBounceVelocity(nextY, CpuPaddleTop);
                nextX = Width - 2;
                nextY = Math.Clamp(Ball.Y + _velocityY, 0, Height - 1);
            }
            else if (nextX > Width - 2)
            {
                Score += 3;
                ResetBall(-1, _velocityY == 0 ? -1 : _velocityY);
                return;
            }
        }

        Ball = new Point(Math.Clamp(nextX, 0, Width - 1), Math.Clamp(nextY, 0, Height - 1));
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
                builder.Append(GetCell(x, y));
            }

            builder.AppendLine("|");
        }

        builder.Append('+').Append(new string('-', Width)).Append('+');
        return builder.ToString();
    }

    internal void SetBallState(Point point, int velocityX, int velocityY)
    {
        Ball = point;
        _velocityX = velocityX;
        _velocityY = velocityY;
    }

    internal void SetPaddlesForTest(int playerTop, int cpuTop)
    {
        PlayerPaddleTop = Math.Clamp(playerTop, 0, Height - PaddleSize);
        CpuPaddleTop = Math.Clamp(cpuTop, 0, Height - PaddleSize);
    }

    private int _velocityX;
    private int _velocityY;

    private void MovePlayer(int delta)
        => PlayerPaddleTop = Math.Clamp(PlayerPaddleTop + delta, 0, Height - PaddleSize);

    private void MoveCpu()
    {
        var cpuCenter = CpuPaddleTop + (PaddleSize / 2);
        if (Ball.Y < cpuCenter)
        {
            CpuPaddleTop = Math.Max(0, CpuPaddleTop - 1);
        }
        else if (Ball.Y > cpuCenter)
        {
            CpuPaddleTop = Math.Min(Height - PaddleSize, CpuPaddleTop + 1);
        }
    }

    private void ResetBall(int directionX, int directionY)
    {
        Ball = new Point(Width / 2, Height / 2);
        _velocityX = directionX < 0 ? -1 : 1;
        _velocityY = directionY switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0
        };
    }

    private char GetCell(int x, int y)
    {
        if (x == Ball.X && y == Ball.Y)
        {
            return 'O';
        }

        if ((x == 1 && IsWithinPaddle(PlayerPaddleTop, y)) || (x == Width - 2 && IsWithinPaddle(CpuPaddleTop, y)))
        {
            return '|';
        }

        return ' ';
    }

    private bool IsWithinPaddle(int paddleTop, int y)
        => y >= paddleTop && y < paddleTop + PaddleSize;

    private int CalculateBounceVelocity(int hitY, int paddleTop)
    {
        var relative = hitY - paddleTop;
        if (relative <= 0)
        {
            return -1;
        }

        if (relative >= PaddleSize - 1)
        {
            return 1;
        }

        return 0;
    }
}