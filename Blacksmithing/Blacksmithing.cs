using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using ItemDataManager;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;

namespace Blacksmithing;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Blacksmithing : BaseUnityPlugin
{
	private const string ModName = "Blacksmithing";
	private const string ModVersion = "1.1.7";
	private const string ModGUID = "org.bepinex.plugins.blacksmithing";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<int> craftingStationLevelIncrease = null!;
	private static ConfigEntry<int> upgradeLevelRequirement = null!;
	private static ConfigEntry<float> durabilityFactor = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	private static Skill blacksmithing = null!;
	private static readonly Harmony harmony = new(ModGUID);

	public void Awake()
	{
		blacksmithing = new Skill("Blacksmithing", "blacksmithing.png");
		blacksmithing.Description.English("Increases the durability of created armor and weapons.");
		blacksmithing.Name.German("Schmiedekunst");
		blacksmithing.Description.German("Erhöht die Haltbarkeit hergestellter Rüstung und Waffen.");
		blacksmithing.Configurable = false;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		craftingStationLevelIncrease = config("2 - Crafting", "Skill Level for Crafting Station Upgrade", 50, new ConfigDescription("Minimum skill level to count as a crafting station upgrade. 0 means disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		upgradeLevelRequirement = config("2 - Crafting", "Skill Level for Extra Upgrade Level", 80, new ConfigDescription("Minimum skill level for an additional upgrade level for armor and weapons. 0 means disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		durabilityFactor = config("2 - Crafting", "Durability Factor", 2f, new ConfigDescription("Factor for durability of armor and weapons at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the blacksmithing skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) => blacksmithing.SkillGainFactor = experienceGainedFactor.Value;
		blacksmithing.SkillGainFactor = experienceGainedFactor.Value;

		Assembly assembly = Assembly.GetExecutingAssembly();
		harmony.PatchAll(assembly);
	}

	private static void ApplyTranspilerToAll(List<MethodInfo> methods, MethodInfo transpiler)
	{
		foreach (MethodInfo method in methods.ToArray())
		{
			void ApplyPatch(ReadOnlyCollection<Patch> patches) => methods.AddRange(patches.Select(p => p.PatchMethod));

			if (Harmony.GetPatchInfo(method) is { } patchInfo)
			{
				ApplyPatch(patchInfo.Prefixes);
				ApplyPatch(patchInfo.Postfixes);
				ApplyPatch(patchInfo.Finalizers);
			}
		}

		HarmonyMethod Transpiler = new(transpiler);

		foreach (MethodInfo method in methods)
		{
			harmony.Patch(method, transpiler: Transpiler);
		}
	}

	private static bool CheckBlacksmithingItem(ItemDrop.ItemData.SharedData item)
	{
		return item.m_itemType is
			       ItemDrop.ItemData.ItemType.Bow or
			       ItemDrop.ItemData.ItemType.Chest or
			       ItemDrop.ItemData.ItemType.Hands or
			       ItemDrop.ItemData.ItemType.Helmet or
			       ItemDrop.ItemData.ItemType.Legs or
			       ItemDrop.ItemData.ItemType.Shield or
			       ItemDrop.ItemData.ItemType.Shoulder or
			       ItemDrop.ItemData.ItemType.TwoHandedWeapon or
			       ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
		       (item.m_itemType is ItemDrop.ItemData.ItemType.OneHandedWeapon && !item.m_attack.m_consumeItem);
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	public class CheckCrafting
	{
		[UsedImplicitly]
		public static void Prefix(InventoryGui __instance, out bool __state)
		{
			__state = __instance.m_craftRecipe.m_item.m_itemData.Data()["Blacksmithing"] is null;
			if (__instance.m_craftRecipe is not null && CheckBlacksmithingItem(__instance.m_craftRecipe.m_item.m_itemData.m_shared))
			{
				__instance.m_craftRecipe.m_item.m_itemData.Data()["Blacksmithing"] = Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor(Skill.fromName("Blacksmithing")) * 100).ToString();
			}
		}

		[UsedImplicitly]
		public static void Finalizer(InventoryGui __instance, bool __state)
		{
			if (__state)
			{
				__instance.m_craftRecipe?.m_item.m_itemData.Data().Remove("Blacksmithing");
			}
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
	public class IncreaseCraftingSkill
	{
		private static bool hasRun = false;

		[UsedImplicitly]
		public static void Postfix()
		{
			if (hasRun)
			{
				return;
			}

			ApplyTranspilerToAll(new List<MethodInfo>
			{
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))
			}, AccessTools.DeclaredMethod(typeof(IncreaseCraftingSkill), nameof(Transpiler)));

			hasRun = true;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			FieldInfo craftsField = AccessTools.DeclaredField(typeof(PlayerProfile.PlayerStats), nameof(PlayerProfile.PlayerStats.m_crafts));
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.opcode == OpCodes.Stfld && instruction.OperandIs(craftsField))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(IncreaseCraftingSkill), nameof(CheckBlacksmithingIncrease)));
				}
			}
		}

		private static void CheckBlacksmithingIncrease()
		{
			if (CheckBlacksmithingItem(InventoryGui.instance.m_craftRecipe.m_item.m_itemData.m_shared))
			{
				Player.m_localPlayer.RaiseSkill("Blacksmithing", 10f);
			}
		}
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool))]
	public class UpdateDurabilityDisplay
	{
		[UsedImplicitly]
		public static void Prefix(bool crafting) => ApplySkillToDurability.skipDurabilityDisplay = crafting;

		[UsedImplicitly]
		public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result, int qualityLevel)
		{
			if (crafting && item.m_shared.m_useDurability)
			{
				float skill = Player.m_localPlayer.GetSkillFactor("Blacksmithing");
				if (skill > 0)
				{
					__result = new Regex("(\\$item_durability.*)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.GetMaxDurability(qualityLevel) * (durabilityFactor.Value - 1))}</color>)");
				}
			}
		}

		[UsedImplicitly]
		public static void Finalizer() => ApplySkillToDurability.skipDurabilityDisplay = false;
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetMaxDurability), typeof(int))]
	public class ApplySkillToDurability
	{
		public static bool skipDurabilityDisplay = false;

		private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
		{
			if (!skipDurabilityDisplay && __instance.Data()["Blacksmithing"] is { } saveSkill)
			{
				__result *= 1 + int.Parse(saveSkill) * blacksmithing.SkillEffectFactor / 100 * (durabilityFactor.Value - 1);
			}
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
	public class IncreaseMaximumUpgradeLevel
	{
		private static bool hasRun = false;

		[UsedImplicitly]
		public static void Postfix()
		{
			if (hasRun)
			{
				return;
			}

			ApplyTranspilerToAll(new List<MethodInfo>
			{
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipeList)),
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.AddRecipeToList)),
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe)),
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.DoCrafting)),
				AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.SetupUpgradeItem))
			}, AccessTools.DeclaredMethod(typeof(IncreaseMaximumUpgradeLevel), nameof(Transpiler)));

			hasRun = true;
		}

		private static readonly FieldInfo maxQualityField = AccessTools.DeclaredField(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_maxQuality));

		private static int MaxQualityIncrease(ItemDrop.ItemData.SharedData sharedData, int currentQuality) => currentQuality + (upgradeLevelRequirement.Value > 0 && Player.m_localPlayer.GetSkillFactor("Blacksmithing") * 100 >= upgradeLevelRequirement.Value && CheckBlacksmithingItem(sharedData) ? 1 : 0);

		[UsedImplicitly]
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.opcode == OpCodes.Ldfld && instruction.OperandIs(maxQualityField))
				{
					yield return new CodeInstruction(OpCodes.Dup);
					yield return instruction;
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(IncreaseMaximumUpgradeLevel), nameof(MaxQualityIncrease)));
				}
				else
				{
					yield return instruction;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Piece.Requirement), nameof(Piece.Requirement.GetAmount), typeof(int))]
	private class IncreaseResourcesRequired
	{
		private static void Postfix(ref int __result, int qualityLevel)
		{
			if (SaveCraftingItemPlayer.item != null && qualityLevel > SaveCraftingItemPlayer.item.m_shared.m_maxQuality)
			{
				__result *= 3;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Recipe), typeof(bool), typeof(int))]
	private class SaveCraftingItemPlayer
	{
		public static ItemDrop.ItemData? item;

		private static void Prefix(Recipe recipe)
		{
			item = recipe.m_item.m_itemData;
		}

		private static void Finalizer() => item = null;
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
	private class SaveCraftingItemInventoryGui
	{
		private static void Prefix()
		{
			SaveCraftingItemPlayer.item = InventoryGui.instance.m_selectedRecipe.Value;
		}

		private static void Finalizer() => SaveCraftingItemPlayer.item = null;
	}

	[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GetLevel))]
	private class IncreaseCraftingStationLevel
	{
		private static void Postfix(CraftingStation __instance, ref int __result)
		{
			if (craftingStationLevelIncrease.Value > 0 && __instance.m_name is "$piece_workbench" or "$piece_forge" && Player.m_localPlayer.GetSkillFactor("Blacksmithing") * 100 >= craftingStationLevelIncrease.Value)
			{
				++__result;
			}
		}
	}
}
