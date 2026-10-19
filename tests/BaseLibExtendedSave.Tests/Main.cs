using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2Mobile.Patches;

public partial class Main : Node
{
    private Assembly _baseLib;
    private Type _modifierSaveType;
    private Type _modifierListType;

    public override void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            Require(args.Length == 4 && (args[2] == "write" || args[2] == "read"),
                "Arguments: BaseLib.dll original-reference-dir write|read case-directory");
            var baseLibPath = Path.GetFullPath(args[0]);
            var referenceDir = Path.GetFullPath(args[1]);
            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(Main).Assembly);
            loadContext.Resolving += (context, name) =>
            {
                var loaded = context.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
                if (loaded != null) return loaded;
                var filename = name.Name + ".dll";
                foreach (var directory in new[] { referenceDir, Path.GetDirectoryName(baseLibPath) })
                {
                    var candidate = Path.Combine(directory, filename);
                    if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
                }
                return null;
            };

            var harmony = new Harmony("sts2.baselib-extended-save.native-tests");
            BaseLibCompatPatches.Apply(harmony);
            // Do not reference BaseLib at compile time: exercise the production
            // AssemblyLoad hook before loading the real user-supplied library.
            _baseLib = loadContext.LoadFromAssemblyPath(baseLibPath);
            _modifierSaveType = _baseLib.GetType("BaseLib.Abstracts.CardModifier+ModifierSave", true);
            _modifierListType = typeof(List<>).MakeGenericType(_modifierSaveType);
            harmony.CreateClassProcessor(_baseLib.GetType("BaseLib.Patches.Saves.ExtendedSaveTypes", true)).Patch();
            _baseLib.GetType("BaseLib.Abstracts.CardModifier", true)
                .GetMethod("RegisterSave", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            _baseLib.GetType("BaseLib.Patches.Saves.ExtendedSavePatches", true)
                .GetMethod("Patch").Invoke(null, new object[] { harmony });

            var contextType = typeof(SerializableCard).Assembly.GetType("MegaCrit.Sts2.Core.Saves.MegaCritSerializerContext", true);
            var context = (JsonSerializerContext)contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null);
            var caseDirectory = Path.GetFullPath(args[3]);
            var savePath = Path.Combine(caseDirectory, "cards.json");
            if (args[2] == "write")
            {
                var cards = CreateCards();
                Directory.CreateDirectory(caseDirectory);
                File.WriteAllText(savePath, JsonSerializer.Serialize(cards, typeof(List<SerializableCard>), context));
                GD.Print($"Saved native BaseLib {_baseLib.GetName().Version} card records to {savePath}");
            }
            else
            {
                var cards = (List<SerializableCard>)JsonSerializer.Deserialize(File.ReadAllText(savePath), typeof(List<SerializableCard>), context);
                CheckCards(cards);
                var legacy = (SerializableCard)JsonSerializer.Deserialize(
                    "{\"id\":\"CARD.STRIKE_IRONCLAD\",\"current_upgrade_level\":1}", typeof(SerializableCard), context);
                Require(legacy.Id == new ModelId("CARD", "STRIKE_IRONCLAD") && legacy.CurrentUpgradeLevel == 1
                    && Modifiers(legacy).Count == 0, "Legacy cards without extended fields must remain readable.");
                GD.Print("PASS: BaseLib modifier identities, amounts and custom properties survive a fresh-process disk roundtrip; cards stay isolated and legacy saves remain readable.");
            }
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }

    private List<SerializableCard> CreateCards()
    {
        var first = new SerializableCard { Id = new ModelId("CARD", "STRIKE_IRONCLAD"), CurrentUpgradeLevel = 1 };
        AddModifier(first, "BITING_COLD", 3, 7, "first-card");
        AddModifier(first, "GILDED", 1, 9, "second-modifier");
        var second = new SerializableCard { Id = new ModelId("CARD", "DEFEND_IRONCLAD") };
        AddModifier(second, "BITING_COLD", 8, 2, "second-card");
        var vanilla = new SerializableCard { Id = new ModelId("CARD", "BASH") };
        return new() { first, second, vanilla };
    }

    private void AddModifier(SerializableCard card, string entry, int amount, int charges, string origin)
    {
        var modifier = Activator.CreateInstance(_modifierSaveType);
        _modifierSaveType.GetProperty("Id").SetValue(modifier, new ModelId("CARD_MODIFIER", "SAVE_TEST-" + entry));
        _modifierSaveType.GetProperty("Amount").SetValue(modifier, amount);
        ((IDictionary<string, int>)_modifierSaveType.GetProperty("IntProperties").GetValue(modifier))["charges"] = charges;
        ((IDictionary<string, string>)_modifierSaveType.GetProperty("AdditionalProperties").GetValue(modifier))["origin"] = origin;
        var values = ModifierDictionary(card);
        if (!values.Contains("BaseLibCardModifiers")) values["BaseLibCardModifiers"] = Activator.CreateInstance(_modifierListType);
        ((IList)values["BaseLibCardModifiers"]).Add(modifier);
    }

    private IDictionary ModifierDictionary(SerializableCard card)
    {
        var handlers = _baseLib.GetType("BaseLib.Patches.Saves.ExtendedSaveHandlers`2", true)
            .MakeGenericType(typeof(CardModel), typeof(SerializableCard));
        var field = handlers.GetProperty("ExtendedData").GetValue(null);
        var data = field.GetType().GetProperty("Item").GetValue(field, new object[] { card });
        return (IDictionary)data.GetType().GetMethod("DictForType").MakeGenericMethod(_modifierListType).Invoke(data, null);
    }

    private IList Modifiers(SerializableCard card)
    {
        var values = ModifierDictionary(card);
        return values.Contains("BaseLibCardModifiers") ? (IList)values["BaseLibCardModifiers"] : Array.Empty<object>();
    }

    private void CheckCards(List<SerializableCard> cards)
    {
        Require(cards.Count == 3, "The full deck must survive the save.");
        Require(cards[0].Id == new ModelId("CARD", "STRIKE_IRONCLAD") && cards[0].CurrentUpgradeLevel == 1,
            "The first card's base identity and upgrade must survive.");
        Require(cards[1].Id == new ModelId("CARD", "DEFEND_IRONCLAD") && cards[1].CurrentUpgradeLevel == 0,
            "The second card must retain its own base state.");
        var first = Modifiers(cards[0]);
        var second = Modifiers(cards[1]);
        Require(first.Count == 2, "Both permanent modifiers must be persisted, not only the vanilla card.");
        Require(second.Count == 1, "A different card must retain only its own modifier.");
        CheckModifier(first[0], "BITING_COLD", 3, 7, "first-card");
        CheckModifier(first[1], "GILDED", 1, 9, "second-modifier");
        CheckModifier(second[0], "BITING_COLD", 8, 2, "second-card");
        Require(cards[2].Id == new ModelId("CARD", "BASH") && Modifiers(cards[2]).Count == 0,
            "Unmodified cards must not acquire another card's modifiers.");
    }

    private void CheckModifier(object modifier, string entry, int amount, int charges, string origin)
    {
        Require((ModelId)_modifierSaveType.GetProperty("Id").GetValue(modifier) == new ModelId("CARD_MODIFIER", "SAVE_TEST-" + entry),
            "Modifier type identity must survive.");
        Require((int)_modifierSaveType.GetProperty("Amount").GetValue(modifier) == amount, "Modifier amount must survive.");
        Require(((IDictionary<string, int>)_modifierSaveType.GetProperty("IntProperties").GetValue(modifier))["charges"] == charges,
            "Integer modifier properties must survive.");
        Require(((IDictionary<string, string>)_modifierSaveType.GetProperty("AdditionalProperties").GetValue(modifier))["origin"] == origin,
            "String modifier properties must survive.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace STS2Mobile.Patches
{
    public static class PatchHelper
    {
        public static void Log(string message) => Console.WriteLine(message);
    }
}
