using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using ExtendedItemDataFramework;
using HarmonyLib;
using SkillManager;
using UnityEngine;

namespace Blacksmithing;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("randyknapp.mods.extendeditemdataframework")]
public class Blacksmithing : BaseUnityPlugin
{
	private const string ModName = "Blacksmithing";
	private const string ModVersion = "1.0.1";
	private const string ModGUID = "org.bepinex.plugins.blacksmithing";

	private static readonly Skill blacksmithing = new("Blacksmithing", "blacksmithing.png");

	public void Awake()
	{
		blacksmithing.Description.English("Increases the durability of created armor and weapons.");
		blacksmithing.Name.German("Schmiedekunst");
		blacksmithing.Description.German("Erhöht die Haltbarkeit hergestellter Rüstung und Waffen.");
		blacksmithing.Configurable = true;

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		ExtendedItemData.NewExtendedItemData += e =>
		{
			if (CheckCrafting.isCrafting && e.m_shared.m_useDurability)
			{
				e.AddComponent<SaveSkill>().skill = Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor(Skill.fromName("Blacksmithing")) * 100);
				e.m_durability = e.GetMaxDurability();
			}
		};
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	public class CheckCrafting
	{
		public static bool isCrafting = false;
		public static void Prefix() => isCrafting = true;
		public static void Finalizer() => isCrafting = false;

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			FieldInfo craftsField = AccessTools.DeclaredField(typeof(PlayerProfile.PlayerStats), nameof(PlayerProfile.PlayerStats.m_crafts));
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.opcode == OpCodes.Stfld && instruction.OperandIs(craftsField))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(CheckCrafting), nameof(CheckBlacksmithingIncrease)));
				}
			}
		}

		private static void CheckBlacksmithingIncrease()
		{
			if (InventoryGui.instance.m_craftRecipe.m_item.m_itemData.m_shared.m_itemType is
			    ItemDrop.ItemData.ItemType.Bow or
			    ItemDrop.ItemData.ItemType.Chest or
			    ItemDrop.ItemData.ItemType.Hands or
			    ItemDrop.ItemData.ItemType.Helmet or
			    ItemDrop.ItemData.ItemType.Legs or
			    ItemDrop.ItemData.ItemType.Shield or
			    ItemDrop.ItemData.ItemType.Shoulder or
			    ItemDrop.ItemData.ItemType.OneHandedWeapon or
			    ItemDrop.ItemData.ItemType.TwoHandedWeapon)
			{
				Player.m_localPlayer.RaiseSkill("Blacksmithing", 10f);
			}
		}
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool))]
	public class UpdateDurabilityDisplay
	{
		public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
		{
			if (crafting && item.m_shared.m_useDurability)
			{
				float skill = Player.m_localPlayer.GetSkillFactor(Skill.fromName("Blacksmithing"));
				if (skill > 0)
				{
					__result = new Regex("(\\$item_durability.*)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.GetMaxDurability())}</color>)");
				}
			}
		}
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetMaxDurability), typeof(int))]
	private class ApplySkillToDurability
	{
		private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
		{
			if (__instance.Extended()?.GetComponent<SaveSkill>() is { } saveSkill)
			{
				__result *= 1 + saveSkill.skill * blacksmithing.SkillEffectFactor / 100;
			}
		}
	}

	public class SaveSkill : BaseExtendedItemComponent
	{
		public int skill;

		public SaveSkill(ExtendedItemData parent) : base(typeof(SaveSkill).AssemblyQualifiedName, parent) { }

		public override string Serialize() => skill.ToString();

		public override void Deserialize(string data) => int.TryParse(data, out skill);

		public override BaseExtendedItemComponent Clone() => (BaseExtendedItemComponent)MemberwiseClone();
	}
}
