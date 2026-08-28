using System;
using Godot;

namespace HeartsAlter.Scripts.InGame.Card;

[Tool]
[GlobalClass]
public partial class CardResource : Resource
{
    [Export(PropertyHint.Dir)]
    private string _facesDirectory = "res://assets/textures/cards/";

    [Export]
    private Godot.Collections.Array<Texture2D> _faces = [];
    
    [Export]
    private Texture2D _back;
    
    [ExportToolButton("自动填充 52 张牌面")]
    public Callable AutoFillButton => Callable.From(AutoFillFaces);

    public Texture2D GetFront(PokerSuit suit, PokerRank rank)
    {
        int index =
            (int)suit * 13 +
            ((int)rank - 2);

        return _faces[index];
    }

    public Texture2D GetBack()
    {
        return _back;
    }

    private void AutoFillFaces()
    {
        _faces.Clear();

        foreach (PokerSuit suit in Enum.GetValues<PokerSuit>())
        {
            for (int rank = 2; rank <= 14; rank++)
            {
                string fileName =
                    $"{SuitToFileName(suit)}{RankToFileName((PokerRank)rank)}.png";

                string path =
                    $"{_facesDirectory}/{fileName}";

                Texture2D texture =
                    GD.Load<Texture2D>(path);

                if (texture == null)
                {
                    GD.PushError($"找不到牌面纹理: {path}");
                    return;
                }

                _faces.Add(texture);
            }
        }

        EmitChanged();

        GD.Print($"CardSkin 自动填充完成，共 {_faces.Count} 张。");
    }

    private static string SuitToFileName(PokerSuit suit)
    {
        return suit switch
        {
            PokerSuit.Club => "Club",
            PokerSuit.Diamond => "Diamond",
            PokerSuit.Heart => "Heart",
            PokerSuit.Spade => "Spade",
            _ => throw new ArgumentOutOfRangeException(nameof(suit), suit, null)
        };
    }

    private static string RankToFileName(PokerRank rank)
    {
        return rank switch
        {
            PokerRank.Jack => "J",
            PokerRank.Queen => "Q",
            PokerRank.King => "K",
            PokerRank.Ace => "A",
            _ => ((int)rank).ToString()
        };
    }
}