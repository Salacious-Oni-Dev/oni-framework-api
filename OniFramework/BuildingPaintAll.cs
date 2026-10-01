using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// Gives every eligible building in the game a paint colour, including buildings this
	/// framework has never heard of.
	///
	/// WHY A PATCH AND NOT A LIST. There are several hundred vanilla buildings and an unbounded
	/// number of modded ones, and a hand-maintained list would be stale the day it was written.
	/// Every building that exists reaches <c>Assets.AddBuildingDef</c> exactly once, so attaching
	/// there covers vanilla, DLC, this project's own buildings and other people's mods with the
	/// same three lines and no registration step for anyone.
	///
	/// WHY <c>Assets.AddBuildingDef</c> RATHER THAN <c>BuildingConfigManager.RegisterBuilding</c>.
	/// Both are universal today and RegisterBuilding is the more obvious hook, but it does not
	/// hand a postfix the <c>BuildingDef</c> it built -- it returns void and keeps the def in a
	/// local -- so reading it back would mean re-deriving it from the config. AddBuildingDef takes
	/// the finished def as its only argument, and is reached on the last line of RegisterBuilding
	/// after <c>DoPostConfigureComplete</c> has run, which is exactly the ordering wanted: a
	/// config that attached its own <see cref="PaintableBuilding"/> with a factory colour has
	/// already done so and is detected rather than duplicated.
	///
	/// KLEI'S OWN HOOK FOR THIS IS DEAD CODE, and it looks alive, so it is worth recording why it
	/// was not used. <c>BuildingConfigManager.AddDefaultBuildingCompleteKComponent(Type)</c> reads
	/// as "add this component to every building". It appends to a field named
	/// <c>defaultKComponents</c>, and the only field ever iterated when components are added is
	/// the differently-named <c>defaultBuildingCompleteKComponents</c>. Nothing reads
	/// <c>defaultKComponents</c> anywhere in the assembly. The sibling
	/// <c>AddBuildingCompleteKComponent(Tag, Type)</c> does work but routes through
	/// <c>GameComps.GetKComponentManager</c>, which is the struct-backed KComponent system and
	/// cannot carry a serialized <c>KMonoBehaviour</c>.
	/// </summary>
	public static class BuildingPaintAll
	{
		private static bool installed;

		/// <summary>
		/// Attach paint to every eligible building from here on. Idempotent, and safe to call
		/// from more than one mod: the second call does nothing, and the patch itself skips a
		/// building that already has the component.
		///
		/// CALL THIS FROM <c>OnLoad</c>. The patch has to be in place before
		/// <c>GeneratedBuildings.LoadGeneratedBuildings</c> runs, which is where vanilla
		/// registers its buildings; a mod's <c>OnLoad</c> is comfortably before that. A building
		/// registered before the patch lands simply never gets the component -- it is not an
		/// error and nothing else misbehaves, it just cannot be painted.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			installed = true;

			harmony.Patch(
				AccessTools.Method(typeof(Assets), nameof(Assets.AddBuildingDef)),
				postfix: new HarmonyMethod(typeof(BuildingPaintAll), nameof(AddBuildingDefPostfix)));

			BuildingPaintSideScreen.Install(harmony);
		}

		/// <summary>
		/// Object layers whose buildings do not get paint.
		///
		/// The dominant filter is <c>BuildingDef.IsTilePiece</c>, which is true whenever a def
		/// sets a <c>TileLayer</c> and therefore already covers tiles, all three conduit
		/// families, power wire, logic wire, travel tubes and ladders -- every building that
		/// draws through <c>KAnimGridTileVisualizer</c> and joins up with its neighbours rather
		/// than drawing its own kanim. Ladders are collateral: they are neither a tile nor a
		/// wire in the sense the exclusion is about, and they are unpaintable anyway because
		/// they share that renderer.
		///
		/// This set adds back the pieces of the same families that are NOT tile pieces: the
		/// bridges and connectors, which are ordinary standalone kanims and would paint fine,
		/// but which sit in a run of pipe or wire that cannot. A painted bridge in an unpainted
		/// pipe run looks like a defect rather than a choice.
		/// </summary>
		private static readonly HashSet<ObjectLayer> ExcludedLayers = new HashSet<ObjectLayer>
		{
			ObjectLayer.GasConduit,
			ObjectLayer.GasConduitTile,
			ObjectLayer.GasConduitConnection,
			ObjectLayer.LiquidConduit,
			ObjectLayer.LiquidConduitTile,
			ObjectLayer.LiquidConduitConnection,
			ObjectLayer.SolidConduit,
			ObjectLayer.SolidConduitTile,
			ObjectLayer.SolidConduitConnection,
			ObjectLayer.Wire,
			ObjectLayer.WireTile,
			ObjectLayer.WireConnectors,
			ObjectLayer.LogicWire,
			ObjectLayer.LogicWireTile,
			ObjectLayer.FoundationTile,
			ObjectLayer.PlasticTile,
			ObjectLayer.LadderTile,
			ObjectLayer.TravelTubeTile,
			ObjectLayer.TravelTubeConnection
		};

		/// <summary>
		/// Whether this building should carry a paint colour. Public because the rule is a
		/// judgement call rather than a fact, and a consumer that disagrees should be able to
		/// read what it is doing before deciding to override it with
		/// <see cref="ShouldPaintOverride"/>.
		/// </summary>
		public static bool IsEligible(BuildingDef def)
		{
			if (def == null || def.BuildingComplete == null)
			{
				return false;
			}

			// Nothing to tint. The symbol channel lives on KBatchedAnimController specifically.
			if (def.BuildingComplete.GetComponent<KBatchedAnimController>() == null)
			{
				return false;
			}

			if (def.IsTilePiece || ExcludedLayers.Contains(def.ObjectLayer))
			{
				return false;
			}

			return true;
		}

		/// <summary>
		/// An optional last word on eligibility, for a consumer that wants a building painted
		/// that <see cref="IsEligible"/> rejects, or the reverse. Returning null means "no
		/// opinion, use the default rule". Set it before <see cref="Install"/> takes effect --
		/// that is, before buildings are registered -- because a building is only offered once.
		/// </summary>
		public static Func<BuildingDef, bool?> ShouldPaintOverride;

		private static void AddBuildingDefPostfix(BuildingDef def)
		{
			try
			{
				if (def == null || def.BuildingComplete == null)
				{
					return;
				}

				// A config that attached its own with a factory colour wins; adding a second
				// component would give the building two paint states and two menu buttons.
				if (def.BuildingComplete.GetComponent<PaintableBuilding>() != null)
				{
					return;
				}

				bool wanted;
				bool? overridden = ShouldPaintOverride != null ? ShouldPaintOverride(def) : null;
				if (overridden.HasValue)
				{
					wanted = overridden.Value;
				}
				else
				{
					wanted = IsEligible(def);
				}

				if (!wanted)
				{
					return;
				}

				def.BuildingComplete.AddOrGet<PaintableBuilding>();
			}
			catch (Exception e)
			{
				// A throw here would abort the registration of whatever building we happened to
				// be looking at, which is a far worse outcome than that building being
				// unpaintable. Named so the building is identifiable from the log.
				FrameworkLog.Warn("BuildingPaintAll could not attach paint to "
					+ (def != null ? def.PrefabID : "<null def>") + ": " + e.Message);
			}
		}
	}
}
