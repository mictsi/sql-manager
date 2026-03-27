using System.Drawing;

namespace SqlManager.Tests;

public sealed class PongGameTests
{
    [Fact]
    public void MovePlayer_ClampsWithinBoard()
    {
        var game = new PongGame();

        for (var step = 0; step < 20; step++)
        {
            game.MovePlayerUp();
        }

        Assert.Equal(0, game.PlayerPaddleTop);

        for (var step = 0; step < 40; step++)
        {
            game.MovePlayerDown();
        }

        Assert.Equal(game.Height - game.PaddleSize, game.PlayerPaddleTop);
    }

    [Fact]
    public void Tick_PlayerBounceIncrementsScore()
    {
        var game = new PongGame();
        game.SetPaddlesForTest(4, 4);
        game.SetBallState(new Point(2, 5), -1, 0);

        game.Tick();

        Assert.Equal(1, game.Score);
        Assert.False(game.IsGameOver);
    }

    [Fact]
    public void Tick_MissEndsGame()
    {
        var game = new PongGame();
        game.SetPaddlesForTest(0, 0);
        game.SetBallState(new Point(1, game.Height - 1), -1, 0);

        game.Tick();

        Assert.True(game.IsGameOver);
    }
}