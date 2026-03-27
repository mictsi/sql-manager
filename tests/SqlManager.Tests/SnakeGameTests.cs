using System.Drawing;

namespace SqlManager.Tests;

public sealed class SnakeGameTests
{
    [Fact]
    public void Tick_MovesSnakeForward()
    {
        var game = new SnakeGame(random: new Random(0));
        var startingHead = game.Segments[0];

        game.Tick();

        var nextHead = game.Segments[0];
        Assert.Equal(new Point(startingHead.X + 1, startingHead.Y), nextHead);
    }

    [Fact]
    public void ChangeDirection_IgnoresImmediateReverse()
    {
        var game = new SnakeGame(random: new Random(0));
        var startingHead = game.Segments[0];

        game.ChangeDirection(SnakeDirection.Left);
        game.Tick();

        Assert.Equal(new Point(startingHead.X + 1, startingHead.Y), game.Segments[0]);
        Assert.Equal(SnakeDirection.Right, game.Direction);
    }

    [Fact]
    public void Tick_EventuallyHitsWallAndEndsGame()
    {
        var game = new SnakeGame(width: 8, height: 8, random: new Random(0));

        for (var step = 0; step < 16 && !game.IsGameOver; step++)
        {
            game.Tick();
        }

        Assert.True(game.IsGameOver);
    }

    [Fact]
    public void RenderBoard_IncludesBordersHeadAndFood()
    {
        var game = new SnakeGame(random: new Random(0));

        var board = game.RenderBoard();

        Assert.Contains("+", board);
        Assert.Contains("@", board);
        Assert.Contains("*", board);
        Assert.Equal(game.Height + 2, board.Split(Environment.NewLine).Length);
    }
}