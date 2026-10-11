using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Internal;
using StardewValley.Menus;

namespace StardewValley;

/// <summary>Opt-in integration probe for the reconstructed game, absent from normal play.</summary>
internal sealed class ReconstructionSmokeProbe : DrawableGameComponent
{
    private readonly string reportPath = Environment.GetEnvironmentVariable("STARDEW_RECONSTRUCTION_REPORT");
    private readonly DateTime started = DateTime.UtcNow;
    private readonly List<object> checks = new();
    private string phase = "startup";
    private int frames;
    private IEnumerator<int> save;
    private string saveName;
    private int savedDay;
    private bool capture;

    public ReconstructionSmokeProbe(Game game) : base(game)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STARDEW_RECONSTRUCTION_DATA_DIR")) || string.IsNullOrWhiteSpace(reportPath))
            throw new InvalidOperationException("Smoke tests require an explicit isolated data directory and report path.");
        DrawOrder = int.MaxValue;
        WriteReport("running");
    }

    public override void Update(GameTime gameTime)
    {
        try
        {
            if ((DateTime.UtcNow - started).TotalSeconds > 180)
                throw new TimeoutException("Timed out during " + phase + "; mode=" + Game1.gameMode + "; newDay=" + Game1.newDay);
            frames++;
            switch (phase)
            {
                case "startup":
                    if (frames < 120 || Game1.activeClickableMenu is not TitleMenu title || Game1.objectData == null)
                        return;
                    Game.Window.Title = "Stardew Valley 1.6.15 - Reconstructed";
                    Check(Game1.objectData.Count == 807, "content_loaded", new { objects = Game1.objectData.Count, graphics = GraphicsDevice.Adapter.Description });
                    CheckItemAndMapRepairs();
                    Game1.resetPlayer();
                    Game1.player.Name = "Reconstruction";
                    Game1.player.farmName.Value = "Rebuilt";
                    Game1.player.favoriteThing.Value = "Source";
                    Game1.player.isCustomized.Value = true;
                    Game1.whichFarm = 0;
                    title.createdNewCharacter(skipIntro: true);
                    SetPhase("new-farm");
                    break;
                case "new-farm":
                    if (frames < 240 || Game1.gameMode != 3 || Game1.newDay || SaveGame.IsProcessing || Game1.dayOfMonth < 1 || Game1.activeClickableMenu != null)
                        return;
                    Check(Game1.getFarm()?.Map != null && Game1.getCharacterFromName("Abigail") != null,
                        "new_farm_loaded", new { locations = Game1.locations.Count, day = Game1.dayOfMonth, location = Game1.currentLocation.Name });
                    Crop crop = new Crop("472", 20, 20, Game1.getFarm());
                    for (int day = 0; day < 4; day++) crop.newDay(1);
                    Check(crop.indexOfHarvest.Value == "24" && crop.currentPhase.Value == crop.phaseDays.Count - 1 && !crop.dead.Value,
                        "parsnip_growth_four_watered_days", new { harvest = crop.indexOfHarvest.Value, phase = crop.currentPhase.Value });
                    Game1.player.addItemToInventory(ItemRegistry.Create("(O)388", 37));
                    Game1.player.Money = 12345;
                    savedDay = Game1.dayOfMonth;
                    save = SaveGame.Save();
                    SetPhase("saving");
                    break;
                case "saving":
                    if (save.MoveNext()) return;
                    save.Dispose();
                    saveName = Game1.GetSaveGameName() + "_" + Game1.uniqueIDForThisGame;
                    Check(File.Exists(Path.Combine(Program.GetSavesFolder(), saveName, saveName)), "save_written", new { folder = saveName });
                    Game1.player.Money = 1;
                    Game1.player.Items.Clear();
                    SaveGame.Load(saveName);
                    SetPhase("loading");
                    break;
                case "loading":
                    if (frames < 180 || Game1.gameMode != 3 || (Game1.currentLoader != null && Game1.currentLoader.Current < 100) || Game1.newDay || SaveGame.IsProcessing)
                        return;
                    int wood = Game1.player.Items.Where(i => i?.QualifiedItemId == "(O)388").Sum(i => i.Stack);
                    Check(Game1.player.Name == "Reconstruction" && Game1.player.Money == 12345 && Game1.dayOfMonth == savedDay && wood == 37,
                        "save_load_roundtrip", new { player = Game1.player.Name, money = Game1.player.Money, day = Game1.dayOfMonth, wood });
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Farm", 64, 15, false);
                    SetPhase("rendering");
                    break;
                case "rendering":
                    if (frames < 180 || Game1.currentLocation.Name != "Farm") return;
                    Game.Window.Title = "Stardew Valley 1.6.15 - Reconstructed - Validation passed";
                    capture = true;
                    Enabled = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            WriteReport("failed", ex.ToString());
            Enabled = false;
            Visible = false;
        }
    }

    private void CheckItemAndMapRepairs()
    {
        foreach (var pair in Game1.objectData)
        {
            var tags = ItemContextTagManager.GetBaseContextTags("(O)" + pair.Key);
            bool geode = pair.Value.GeodeDropsDefaultItems || pair.Value.GeodeDrops?.Count > 0 || pair.Value.ContextTags?.Contains("geode") == true;
            if (tags.Contains("geode") != geode || (!pair.Value.CanBeGivenAsGift && !tags.Contains("not_giftable")))
                throw new InvalidOperationException("Context tags differ from object data: " + pair.Key);
        }
        Check(true, "all_object_context_tags", new { count = Game1.objectData.Count });
        GameLocation location = new GameLocation { map = new xTile.Map() };
        location.Map.Properties["probe-empty"] = "";
        location.Map.Properties["probe-value"] = "value";
        Check(!location.HasMapPropertyWithValue("absent") && !location.HasMapPropertyWithValue("probe-empty") && location.HasMapPropertyWithValue("probe-value"),
            "map_property_overload", new { absent = false, empty = false, nonempty = true });
        var items = new List<Item> { ItemRegistry.Create("(O)388"), ItemRegistry.Create("(O)390") };
        int visited = 0;
        bool Visit(in ForEachItemContext context)
        {
            var path = context.GetPath();
            if (path.Count != 2 || !Equals(path[0], "probe") || !ReferenceEquals(path[1], items))
                throw new InvalidOperationException("Item traversal lost the captured parent/list path");
            visited++;
            if (context.Item.QualifiedItemId == "(O)390") context.RemoveItem();
            else context.ReplaceItemWith(ItemRegistry.Create("(O)24"));
            return true;
        }
        ForEachItemHelper.ApplyToList(items, Visit, () => new List<object> { "probe" });
        Check(visited == 2 && items.Count == 1 && items[0].QualifiedItemId == "(O)24", "item_traversal_edit_and_path", new { visited });
    }

    public override void Draw(GameTime gameTime)
    {
        if (!capture) return;
        try
        {
            var presentation = GraphicsDevice.PresentationParameters;
            var pixels = new Color[presentation.BackBufferWidth * presentation.BackBufferHeight];
            GraphicsDevice.GetBackBufferData(pixels);
            using var texture = new Texture2D(GraphicsDevice, presentation.BackBufferWidth, presentation.BackBufferHeight);
            texture.SetData(pixels);
            using var output = File.Create(Path.Combine(Path.GetDirectoryName(reportPath), "game.png"));
            texture.SaveAsPng(output, texture.Width, texture.Height);
            Check(pixels.Any(p => p.R > 40 || p.G > 40 || p.B > 40), "farm_rendered", new { texture.Width, texture.Height });
            phase = "complete";
            WriteReport("passed");
        }
        catch (Exception ex) { WriteReport("failed", ex.ToString()); }
        capture = false;
        Visible = false;
    }

    private void Check(bool passed, string name, object detail)
    {
        checks.Add(new { name, passed, detail });
        if (!passed) throw new InvalidOperationException("Failed check: " + name);
        WriteReport("running");
    }

    private void SetPhase(string next) { phase = next; frames = 0; WriteReport("running"); }

    private void WriteReport(string status, string error = null)
    {
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status, phase, started, elapsedSeconds = (DateTime.UtcNow - started).TotalSeconds, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
