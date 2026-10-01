using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// THE LIVE GATE FOR THE THREE-REGISTRY FACADE. Every claim
	/// <see cref="SimExtCellProperties"/>, <see cref="SimExtElementAttributes"/>,
	/// <see cref="SimExtEventStreams"/> and <see cref="SimExtFrame"/> make, exercised against a
	/// real running SimDLL and asserted.
	///
	/// <b>Why the facade needs one at all when the native side has 365 offline assertions.</b>
	/// Everything this layer can get wrong is invisible to both a compiler and to
	/// <c>vftest</c>: a payload field at the wrong offset, a descriptor read with the wrong
	/// stride, a <c>CharSet</c> that marshals a name as UTF-16 into a <c>const char*</c>, a
	/// Harmony postfix whose parameter names do not match and which therefore never fires. Each
	/// of those produces a facade that compiles, loads, logs nothing, and silently does not
	/// work. So the assertions live here and run in-game.
	///
	/// <b>Split in three, because the sim's own lifecycle splits it.</b> <see cref="Install"/>
	/// runs at <c>OnLoad</c> and only SUBSCRIBES: the registrations themselves happen in the
	/// window <see cref="SimExtRegistrar"/> opens, between <c>SIM_Initialize</c> and the world
	/// allocation, because before that the SimDLL has no state to register into and after it
	/// registration is closed. Then the writes are queued, so they have to be sent a tick before
	/// they can be read: <see cref="Provoke"/> sends, <see cref="Verify"/> reads, and a rig puts
	/// a couple of ticks between them.
	///
	/// <b>The first live run is what taught this file that.</b> It registered at <c>OnLoad</c> --
	/// which the native ABI's own comment suggested and this facade's first draft documented --
	/// and every registration came back with the "no registry here" sentinel, because
	/// <c>SIM_Initialize</c> had not run yet. That is precisely the class of failure an offline
	/// suite cannot have: offline, the sim is always initialised before anything is sent to it.
	///
	/// <b>Inert unless armed.</b> Nothing here runs, registers or subscribes without
	/// <see cref="Flag"/> on the command line, so a player's launch never carries a test
	/// property in its saves.
	/// </summary>
	public static class SimExtSelfTest
	{
		/// <summary>Command-line flag that arms the whole thing.</summary>
		public const string Flag = "--ext-selftest";

		/// <summary>The consumer identity the test registers under. Folds to owner <c>selftest</c>.</summary>
		public const string Consumer = "selftest";

		private const string ProbeLeaf = "probe";
		private const string AttributeLeaf = "attr";

		/// <summary>
		/// A second cell property that is REGISTERED AND DELIBERATELY NEVER PUBLISHED.
		///
		/// It exists for a gap the rest of this self-test cannot reach.
		/// <c>DebugBulkRoutes</c>'s <c>/extcells?published=1</c> holds an idle LEASE on anything
		/// it subscribed itself, and never on anything a mod published -- renewing or expiring a
		/// mod's publication would let one route's housekeeping silently stop another consumer's
		/// data, as it could for an event stream.
		/// That protection is testable against <see cref="ProbeLeaf"/>, which this class
		/// publishes. The OTHER half -- that the route's own lease holds across a slow poll and
		/// still expires when nobody is asking -- needs a property the route can actually own,
		/// and every property on a self-test run was published by the self-test.
		///
		/// So: this one is registered and left alone. A route that subscribes it owns it.
		/// </summary>
		private const string UnpublishedLeaf = "unheld";

		/// <summary>The default every component of the probe property starts at. Deliberately not
		/// 0, so "still at the default" and "zeroed" are distinguishable.</summary>
		public const float ProbeDefault = 1.5f;

		/// <summary>The value written into lane 0 of the probe cell.</summary>
		public const float ProbeWritten = 12.5f;

		/// <summary>An out-of-range property index, used to provoke a refusal on purpose.</summary>
		private const int NonsenseProperty = 9999;

		/// <summary>The property the field solver's rule is attached to. Its own
		/// property rather than <see cref="ProbeLeaf"/>, because a field needs arity 1 and the
		/// probe is arity 2 -- which is itself one of the refusals asserted below.</summary>
		private const string FieldLeaf = "field";

		/// <summary>Whether <see cref="Install"/> found the flag and armed.</summary>
		public static bool Armed { get; private set; }

		/// <summary>The owner token minted for <see cref="Consumer"/>.</summary>
		public static ExtOwner Owner { get; private set; }

		/// <summary>Index of the registered <c>selftest.probe</c> cell property, or negative.</summary>
		public static int ProbeProperty { get; private set; }

		/// <summary>Index of the registered <c>selftest.attr</c> element attribute, or negative.</summary>
		public static int ProbeAttribute { get; private set; }

		/// <summary>What a duplicate registration of the same probe name came back with.</summary>
		public static int DuplicateResult { get; private set; }

		/// <summary>Index of <c>selftest.field</c>, the arity-1 float a field is attached to.</summary>
		public static int FieldProperty { get; private set; }

		/// <summary>The field index <see cref="SimFields.Register"/> returned, or negative.</summary>
		public static int FieldIndex { get; private set; }

		/// <summary>What attaching a SECOND field to the same property came back with.</summary>
		public static int FieldDuplicateResult { get; private set; }

		/// <summary>What attaching a field to the arity-2 probe property came back with.</summary>
		public static int FieldBadArityResult { get; private set; }

		/// <summary>
		/// Index of <c>selftest.unheld</c>, registered and never published. See
		/// <see cref="UnpublishedLeaf"/> for what it is for.
		/// </summary>
		public static int UnpublishedProperty { get; private set; }

		/// <summary>The game cell the probe property is written to.</summary>
		public static int ProbeCell { get; private set; }

		/// <summary>
		/// <see cref="SimExtPhases.Count"/> as read at <c>OnLoad</c> -- before
		/// <c>SIM_Initialize</c> exists, let alone a world. See
		/// <see cref="phaseTableAtLoad"/> for why that moment is the interesting one.
		/// </summary>
		private static int phaseCountAtLoad = -1;

		/// <summary>
		/// The phase names as read at <c>OnLoad</c>, joined.
		///
		/// The phase table's headline property is that it is readable BEFORE a world exists --
		/// it is compile-time, takes no worker barrier, and is meant to let a mod check at load
		/// time that the DLL it just loaded still orders phases the way the build it was written
		/// against did. That claim cannot be tested from <see cref="Verify"/>, where a world has
		/// existed for thousands of ticks. So it is captured here instead and compared later:
		/// if the two disagree, the table is world state after all and the ABI's promise is
		/// wrong.
		/// </summary>
		private static string phaseTableAtLoad;

		/// <summary>
		/// <see cref="SimExtMessages.Count"/> as read at <c>OnLoad</c>, for the same reason
		/// <see cref="phaseCountAtLoad"/> is: the message table makes the same
		/// readable-before-a-world claim the phase table does, and the only place to test it is
		/// the moment before <c>SIM_Initialize</c>.
		/// </summary>
		private static int messageCountAtLoad = -1;

		/// <summary>The message names as read at <c>OnLoad</c>, joined. See
		/// <see cref="phaseTableAtLoad"/>.</summary>
		private static string messageTableAtLoad;

		/// <summary>
		/// The cell the room arms probe, chosen at <see cref="Provoke"/> time by looking for a gas
		/// cell rather than hardcoded, so this does not depend on any one blueprint's geometry.
		/// -1 if the canvas held no gas cell at all, which is itself asserted.
		/// </summary>
		private static int roomProbeCell = -1;

		/// <summary>The ElementTable index injected at <see cref="roomProbeCell"/>.</summary>
		private static int roomProbeSpecies = -1;

		/// <summary>
		/// How much is injected to bring the room graph into existence. Small enough to change
		/// nothing a rig cares about, large enough that its own conservation is checkable against
		/// a per-cell re-derivation without fighting float noise.
		/// </summary>
		private const float RoomProbeMassKg = 0.25f;

		private static bool refusalStreamFound;
		private static bool rehydrationListedUsBeforeWrite;
		private static bool provoked;

		// Refusals accumulate across ticks rather than being read on one, because a record
		// reaches exactly ONE published frame and the frame it reaches depends on which side of
		// the message boundary it was produced on. Reading a single tick and finding nothing
		// would be a timing artefact reported as a failure.
		private static readonly List<ExtRefusedMessage> refusals = new List<ExtRefusedMessage>();

		/// <summary>
		/// Arm the self-test. Call from a consumer's <c>UserMod2.OnLoad</c>; returns immediately
		/// unless <see cref="Flag"/> is on the command line.
		///
		/// This mints the owner and installs the two hooks. It deliberately registers NOTHING:
		/// the SimDLL does not exist yet at <c>OnLoad</c>, so everything that talks to it is
		/// deferred to <see cref="OnRegistrationWindow"/>.
		/// </summary>
		public static void Install(Harmony harmony)
		{
			if (harmony == null)
			{
				throw new ArgumentNullException("harmony");
			}
			if (Armed || Array.IndexOf(Environment.GetCommandLineArgs(), Flag) < 0)
			{
				return;
			}
			Armed = true;

			ExtOwner owner;
			// This file is a consumer of the framework like any other even though it ships inside
			// it, so it declares the API level it needs like any other.
			FrameworkVersion.Require(0, 1, Consumer, out owner);
			Owner = owner;
			Log("owner token = \"" + owner + "\" (valid=" + owner.IsValid + ")");

			// DECLARED HERE, AT ARMING, BEFORE ANYTHING IS PROVOKED. Two of this file's
			// assertions work by making the sim complain, and ONI opens its crash dialog for any
			// logged error -- so a perfectly green EXTREGISTRY run pops "Oops-a-daisy! ... uncheck
			// all of the mods below". That screen is wrong twice over here: the error is the test
			// passing, and the mods it asks you to switch off are the thing under test.
			// RigCrashScreen puts these two lines on the dialog instead, so the reader can match
			// the message against what was asked for rather than guess.
			RigCrashScreen.DeclareExpectedError("selftest",
				"registers the extension property \"" + Owner.Compose(ProbeLeaf) + "\" TWICE. The "
				+ "sim must refuse the second one as a duplicate -- a registry that silently reset "
				+ "a live property would be far worse than one that complains -- so the complaint "
				+ "is the assertion.");
			RigCrashScreen.DeclareExpectedError("selftest",
				"writes to an UNREGISTERED property index on purpose, to prove the "
				+ "sim.message_refused stream reports a dropped message instead of dropping it "
				+ "in silence.");


			SimExtRegistrar.Install(harmony);
			SimExtRegistrar.Opening += OnRegistrationWindow;

			SimExtFrame.Install(harmony);
			SimExtFrame.Bound += OnFrameBound;

			// READ THE PHASE TABLE HERE, at OnLoad, on purpose. This is the earliest moment a
			// mod runs at, `SIM_Initialize` has not been called, and there is no world -- which
			// is exactly the situation the phase exports claim to be legal in. Verify() reads it
			// again once the world is thousands of ticks old and compares.
			phaseCountAtLoad = SimExtPhases.Count;
			phaseTableAtLoad = JoinPhaseNames(SimExtPhases.All());
			Log("phase table at OnLoad, before SIM_Initialize: " + SimExtPhases.Summary());

			// And the message table, which makes the same claim and is read at the same moment.
			messageCountAtLoad = SimExtMessages.Count;
			messageTableAtLoad = JoinMessageNames(SimExtMessages.All());
			Log("message table at OnLoad, before SIM_Initialize: " + SimExtMessages.Summary());
		}

		private static string JoinPhaseNames(SimExtPhases.Descriptor[] phases)
		{
			string[] names = new string[phases.Length];
			for (int i = 0; i < phases.Length; i++)
			{
				names[i] = phases[i].Name;
			}
			return string.Join(",", names);
		}

		private static string JoinMessageNames(SimExtMessages.Descriptor[] messages)
		{
			string[] names = new string[messages.Length];
			for (int i = 0; i < messages.Length; i++)
			{
				names[i] = messages[i].Name;
			}
			return string.Join(",", names);
		}

		/// <summary>
		/// Everything that talks to the SimDLL. Runs in <see cref="SimExtRegistrar.Opening"/> --
		/// after <c>SIM_Initialize</c> has built the registries and before the world allocation
		/// closes registration -- and runs AGAIN on every load, because the sim it registered
		/// into no longer exists by then.
		/// </summary>
		/// <summary>
		/// Field-for-field and slot-for-slot equality of two aggregates. Written out rather than
		/// memcmp'd because the managed struct is a decode of the native one, not a copy of its
		/// bytes: comparing the bytes here would compare padding this code chose, and would pass
		/// while a field it forgot to decode stayed zero in both.
		/// </summary>
		private static bool RoomAggregatesMatch(SimRooms.RoomAggregate a, SimRooms.RoomAggregate b)
		{
			if (a.RoomId != b.RoomId || a.CellCount != b.CellCount || a.AwakeCount != b.AwakeCount
				|| a.Owned != b.Owned || a.MixtureCellCount != b.MixtureCellCount
				|| a.SpeciesCount != b.SpeciesCount || a.SpeciesOverflow != b.SpeciesOverflow
				|| a.MixtureMassKg != b.MixtureMassKg || a.VanillaMassKg != b.VanillaMassKg
				|| a.MeanPressurePa != b.MeanPressurePa || a.TemperatureK != b.TemperatureK)
			{
				return false;
			}
			for (int i = 0; i < a.SpeciesCount; i++)
			{
				if (a.SpeciesAt(i) != b.SpeciesAt(i) || a.MassKgAt(i) != b.MassKgAt(i))
				{
					return false;
				}
			}
			return true;
		}

		private static void OnRegistrationWindow()
		{
			provoked = false;
			refusals.Clear();

			ProbeProperty = SimExtCellProperties.RegisterFloat(Owner, ProbeLeaf,
				ExtPersistence.Rehydrated, ProbeDefault, 2);
			Log("registered " + Owner.Compose(ProbeLeaf) + " -> index " + ProbeProperty
				+ (ProbeProperty < 0
					? " (" + SimExtRegistry.DescribeRefusal(ProbeProperty) + ")" : ""));

			// The same name a second time. A registry that silently reset an existing property
			// would be far worse than one that refuses, so the refusal is an assertion, not a
			// diagnostic.
			DuplicateResult = SimExtCellProperties.RegisterFloat(Owner, ProbeLeaf,
				ExtPersistence.Rehydrated, ProbeDefault, 2);
			Log("duplicate registration returned " + DuplicateResult);

			ProbeAttribute = SimExtElementAttributes.Register(Owner, AttributeLeaf,
				ExtScalarType.Float32);
			Log("registered element attribute " + Owner.Compose(AttributeLeaf) + " -> index "
				+ ProbeAttribute);

			refusalStreamFound = SimExtEventStreams.SubscribeToRefusals(true);
			Log("sim.message_refused subscribed = " + refusalStreamFound);

			UnpublishedProperty = SimExtCellProperties.RegisterFloat(Owner, UnpublishedLeaf,
				ExtPersistence.Rehydrated, ProbeDefault, 1);
			Log("registered " + Owner.Compose(UnpublishedLeaf) + " -> index "
				+ UnpublishedProperty + " (deliberately NOT published -- see UnpublishedLeaf)");

			if (ProbeProperty >= 0)
			{
				SimExtCellProperties.Publish(ProbeProperty, true);
			}

			// NOTHING PUBLISHES UnpublishedProperty. That is the point of it, and it is asserted
			// below rather than left to a reader to notice the absence of a line.

			// ---------------------------------------------------- the field solver
			//
			// A field is a property plus a rule, so this registers the property the ordinary way
			// and then attaches the rule -- which is the whole API shape, exercised rather than
			// described. Registered HERE, in the same window, because kRegisterField is immediate
			// for the same reason the property registration is: registration closes at the first
			// world allocation.
			FieldProperty = SimExtCellProperties.RegisterFloat(Owner, FieldLeaf,
				ExtPersistence.Rehydrated, 0f, 1);
			Log("registered " + Owner.Compose(FieldLeaf) + " -> index " + FieldProperty);

			if (FieldProperty >= 0)
			{
				// No attenuation and no source: the field is IDLE, so it costs the sim a skipped
				// loop header per region per substep and cannot change a single cell. A self-test
				// that armed a real source would be a self-test that alters the world it is
				// measuring -- and this one runs on the EXTREGISTRY rig beside checks that assert
				// exact cell values.
				FieldIndex = SimFields.Register(FieldProperty, SimFields.NoAttribute, 0f,
					FieldAttenuationLaw.Flat, FieldCombine.Transmission, 1f);
				Log("attached a field to it -> index " + FieldIndex
					+ (FieldIndex < 0 ? " (" + SimExtRegistry.DescribeRefusal(FieldIndex) + ")"
						: ""));

				// A second rule on the same property. One field per property, and a registry that
				// silently replaced the first rule would be far worse than one that refuses.
				FieldDuplicateResult = SimFields.Register(FieldProperty, SimFields.NoAttribute, 0f,
					FieldAttenuationLaw.Flat, FieldCombine.Transmission, 1f);
				Log("a second field on the same property returned " + FieldDuplicateResult);
			}

			// The arity-2 probe property. A field is one float per cell, and this is the refusal
			// that says so -- reachable only because the probe is deliberately arity 2.
			if (ProbeProperty >= 0)
			{
				FieldBadArityResult = SimFields.Register(ProbeProperty, SimFields.NoAttribute, 0f,
					FieldAttenuationLaw.Flat, FieldCombine.Transmission, 1f);
				Log("a field on the arity-2 probe property returned " + FieldBadArityResult);
			}
		}

		private static void OnFrameBound()
		{
			List<ExtRefusedMessage> tick = SimExtEventStreams.ReadRefusals();
			for (int i = 0; i < tick.Count; i++)
			{
				refusals.Add(tick[i]);
			}
		}

		/// <summary>
		/// Send everything that has to travel through the message queue: the probe writes and one
		/// deliberately malformed write. Call once the world exists; the reads in
		/// <see cref="Verify"/> need at least one full tick after this.
		/// </summary>
		public static void Provoke()
		{
			if (!Armed || provoked)
			{
				return;
			}
			provoked = true;

			// Captured BEFORE the first write, because that is the only moment the answer is
			// interesting: a kRehydrated property nobody has written yet is exactly what
			// OutstandingRehydration is supposed to name.
			int[] outstanding = SimExtCellProperties.OutstandingRehydration();
			rehydrationListedUsBeforeWrite = Array.IndexOf(outstanding, ProbeProperty) >= 0;
			Log("outstanding rehydration before any write: [" + string.Join(", ",
				Array.ConvertAll(outstanding, i => i.ToString())) + "]");

			ProbeCell = Grid.CellCount / 2;
			SimExtCellProperties.SetFloat(ProbeProperty, ProbeCell, ProbeWritten);

			// The deliberate refusal. An unregistered property index is the one refusal the sim
			// wires to the stream at kSetCellProperty, so this is what proves the diagnostic
			// route works end to end rather than merely existing.
			SimExtCellProperties.SetFloat(NonsenseProperty, ProbeCell, 1f);
			Log("provoked: wrote lane 0 of cell " + ProbeCell + " and one deliberately bad write "
				+ "against property index " + NonsenseProperty);

			// THE ROOM GRAPH HAS TO BE MADE TO EXIST, and this is the whole reason the room arms
			// live behind Provoke rather than being read cold. sim/gas_rooms.h builds the graph
			// for the MIXING kernel, and mixing is activated for a world by the first
			// kInjectGasSpecies it ever receives -- so on a canvas nobody has injected into,
			// SIM_RoomAggregate correctly answers "no room" for every cell, on a DLL that exports
			// it perfectly well, and a gate that only saw that would never once exercise the
			// decode it exists to check.
			//
			// So: find a gas cell, inject a quarter kilogram of WHAT IS ALREADY THERE into it,
			// and let the two checkpoints after this call mix it. Injecting the cell's own species
			// keeps this from being a change to the world's contents in any sense a rig would
			// notice; what it does change is that this world now runs the mixing kernel for the
			// rest of the run, which is stated here rather than discovered. Nothing is promoted:
			// an unpromoted room is the honest default case and the arms assert it.
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (!Grid.IsValidCell(cell))
				{
					continue;
				}
				Element element = Grid.Element[cell];
				if (element == null || !element.IsGas || Grid.Mass[cell] <= 0f)
				{
					continue;
				}
				int speciesIdx = ElementLoader.elements.IndexOf(element);
				if (speciesIdx < 0)
				{
					continue;
				}
				roomProbeCell = cell;
				roomProbeSpecies = speciesIdx;
				break;
			}
			if (roomProbeCell >= 0)
			{
				float temperatureK = Grid.Temperature[roomProbeCell];
				if (temperatureK <= 0f)
				{
					temperatureK = 293.15f;
				}
				GasMixtureFacade.Inject(roomProbeCell, roomProbeSpecies, RoomProbeMassKg,
					temperatureK);
				Log("room probe: injected " + RoomProbeMassKg + " kg of species "
					+ roomProbeSpecies + " into cell " + roomProbeCell + " at "
					+ temperatureK.ToString("F2") + " K, to bring the room graph into existence");
			}
			else
			{
				Log("room probe: found no gas cell on this canvas -- the room arms will say so");
			}
		}

		/// <summary>
		/// Assert every claim. <paramref name="check"/> is the rig's own <c>Check(bool, string)</c>
		/// and <paramref name="log"/> its <c>Log(string)</c>, so the verdict lands in the rig's
		/// transcript and is counted by the harness rather than by anything here.
		/// </summary>
		public static void Verify(Func<bool, string, bool> check, Action<string> log)
		{
			if (check == null)
			{
				throw new ArgumentNullException("check");
			}
			if (log == null)
			{
				log = Log;
			}
			if (!Armed)
			{
				check(false, "the self-test was armed (" + Flag + " on the command line)");
				return;
			}

			// -------------------------------------------------- registry 1: registration
			check(ProbeProperty >= 0, "a mod can register a per-cell property (selftest.probe -> "
				+ "index " + ProbeProperty + ")");
			check(DuplicateResult == -(int)ExtRegisterResult.Duplicate,
				"registering the same name twice is refused as a duplicate rather than silently "
				+ "resetting the property (got " + DuplicateResult + ")");
			check(SimExtCellProperties.Find(Owner.Compose(ProbeLeaf)) == ProbeProperty,
				"the property resolves back to the same index by name");
			check(SimExtCellProperties.Find("nosuch.property") < 0,
				"an unregistered name resolves to -1 rather than to a plausible index");

			// -------------------------------------------------- registry 1: read-back
			float lane0;
			bool read0 = SimExtCellProperties.TryReadFloat(ProbeProperty, ProbeCell, out lane0, 0);
			check(read0 && Math.Abs(lane0 - ProbeWritten) < 1e-4f,
				"the per-call read returns what was written (" + lane0 + " vs " + ProbeWritten + ")");

			float lane1;
			bool read1 = SimExtCellProperties.TryReadFloat(ProbeProperty, ProbeCell, out lane1, 1);
			check(read1 && Math.Abs(lane1 - ProbeDefault) < 1e-4f,
				"an untouched lane of the same cell still holds the registered default ("
				+ lane1 + " vs " + ProbeDefault + ")");

			float elsewhere;
			int otherCell = ProbeCell + 1;
			bool readOther = SimExtCellProperties.TryReadFloat(ProbeProperty, otherCell,
				out elsewhere, 0);
			check(readOther && Math.Abs(elsewhere - ProbeDefault) < 1e-4f,
				"a cell nobody wrote still holds the registered default, so the write landed on "
				+ "one cell and not on the array (" + elsewhere + ")");

			check(rehydrationListedUsBeforeWrite,
				"OutstandingRehydration named the property while it was still unwritten -- the "
				+ "kRehydrated promise is checkable, not decorative");
			check(Array.IndexOf(SimExtCellProperties.OutstandingRehydration(), ProbeProperty) < 0,
				"and stops naming it once a cell has been written");

			// The unpublished twin: registered like any other, absent from the published table.
			// Asserted rather than assumed, because the whole value of this fixture to
			// `/extcells?published=1` is that the route -- and not this class -- is the thing
			// that owns it, and a property this class had accidentally published would make the
			// lease test silently measure the foreign-ownership path instead.
			SimExtFrame.PublishedProperty unheld;
			check(UnpublishedProperty >= 0,
				"selftest.unheld registered (index " + UnpublishedProperty + ")");
			check(UnpublishedProperty != ProbeProperty,
				"and got an index of its own, distinct from selftest.probe");
			check(!SimExtFrame.TryGetProperty(Owner.Compose(UnpublishedLeaf), out unheld),
				"selftest.unheld is NOT in the published table -- nothing here published it, so "
				+ "a debug route that subscribes it owns it and may expire it");

			// -------------------------------------------------- stage 3: the published table
			SimExtFrame.PublishedProperty published;
			bool found = SimExtFrame.TryGetProperty(Owner.Compose(ProbeLeaf), out published);
			check(SimExtFrame.TickId > 0,
				"the per-tick binder fired at all (TickId = " + SimExtFrame.TickId + ")");
			check(found, "the subscribed property appears in the published descriptor table");
			if (found)
			{
				check(published.IsCurrent,
					"its descriptor belongs to the tick in progress rather than a stale frame");
				check(published.Type == ExtScalarType.Float32 && published.Arity == 2
						&& published.Stride == 4,
					"the descriptor reports the shape it was registered with (type="
					+ published.Type + " arity=" + published.Arity + " stride=" + published.Stride + ")");

				int padded = (Grid.WidthInCells + 2) * (Grid.HeightInCells + 2);
				check(published.CellCount == padded,
					"the descriptor's cell count is the PADDED grid (" + published.CellCount
					+ " vs " + padded + ")");
				check(published.ByteCount == published.CellCount * published.Arity * published.Stride,
					"byteCount is cellCount * arity * stride and needs no deriving");

				float viaFrame;
				bool readFrame = published.TryReadFloat(SimExtFrame.PaddedCell(ProbeCell),
					out viaFrame, 0);
				check(readFrame && Math.Abs(viaFrame - ProbeWritten) < 1e-4f,
					"the published span and the per-call export agree about the same cell ("
					+ viaFrame + " vs " + lane0 + ")");
			}

			// -------------------------------------------------- registry 2: element attributes
			check(ProbeAttribute >= 0, "a mod can register a per-element attribute (selftest.attr "
				+ "-> index " + ProbeAttribute + ")");

			SimExtElementAttributes.SetFloat(ProbeAttribute, SimHashes.Oxygen, 42.5f);
			float attrValue;
			bool attrRead = SimExtElementAttributes.TryReadFloat(ProbeAttribute, SimHashes.Oxygen,
				out attrValue);
			check(attrRead && Math.Abs(attrValue - 42.5f) < 1e-4f,
				"an element attribute write is IMMEDIATE -- readable on the same tick, with no "
				+ "frame in between (" + attrValue + ")");

			float unrelated;
			check(!SimExtElementAttributes.TryReadFloat(ProbeAttribute, SimHashes.Hydrogen,
					out unrelated),
				"another element has no entry for it, so the store is sparse rather than dense");

			SimExtElementAttributes.SetFloat(ProbeAttribute, SimHashes.Oxygen, 0f);
			float storedZero;
			check(SimExtElementAttributes.TryReadFloat(ProbeAttribute, SimHashes.Oxygen,
					out storedZero) && storedZero == 0f,
				"a stored 0 reads back as SET, so zero is a value and not a way of saying unset");

			SimExtElementAttributes.Clear(ProbeAttribute, SimHashes.Oxygen);
			float cleared;
			check(!SimExtElementAttributes.TryReadFloat(ProbeAttribute, SimHashes.Oxygen,
					out cleared),
				"and clearing it puts the element back to UNSET, which is a different answer");

			// -------------------------------------------------- the first first-party attribute
			int molecular = SimExtElementAttributes.MolecularMassIndex;
			check(molecular >= 0, "the sim's own sim.molecular_mass attribute resolves ("
				+ molecular + ")");
			float oxygenMass;
			bool haveOxygen = SimExtElementAttributes.TryGetMolecularMass(SimHashes.Oxygen,
				out oxygenMass);
			check(haveOxygen && Math.Abs(oxygenMass - 31.9988f) < 1e-3f,
				"Oxygen's molecular mass reads back as O2's 31.9988 g/mol, not Klei's atomic "
				+ "15.9994 -- the diatomic correction, visible from managed code for the first "
				+ "time (" + oxygenMass + ")");
			float viaMaterial;
			check(MaterialPropertyRegistry.TryGetSimMolecularMass(SimHashes.Oxygen, out viaMaterial)
					&& Math.Abs(viaMaterial - oxygenMass) < 1e-6f,
				"and MaterialPropertyRegistry reports the same number, so the registry route and the "
				+ "material registry are not two answers");

			// Physical latent heats: the sixth first-party attribute
			// resolves, and to its own index -- not the curve's, whose fifth slot is the OTHER
			// latent heat and is exactly what a mis-resolved name would silently read.
			int fusionIndex = SimExtElementAttributes.LatentFusionIndex;
			check(fusionIndex >= 0 && fusionIndex != SimExtElementAttributes.PhaseCurveIndex,
				"the sim's own sim.latent_fusion attribute resolves, at an index of its own ("
				+ fusionIndex + ")");


			// -------------------------------------------------- stage 5b: enumerating registry 2
			//
			// The arms above all name an attribute the caller already had. These are the ones a
			// TOOL can run: what does this sim hold, and which elements carry a value. Until
			// stage 5b there was no answer, and no managed-side ledger could have substituted
			// one -- sim.molecular_mass is registered natively in the sim's own constructor, so
			// a list built from registrations that went through this facade would miss the only
			// attribute a stock custom build has.
			check(SimExtElementAttributes.CanEnumerate,
				"this SimDLL can enumerate its element attributes (stage 5b's three exports)");

			List<SimExtElementAttributes.Descriptor> attributes =
				SimExtElementAttributes.Describe();
			check(attributes.Count >= 2, "enumeration finds at least the sim's own attribute and "
				+ "this self-test's (" + attributes.Count + " registered)");

			bool foundProbe = false;
			bool foundMolecular = false;
			foreach (SimExtElementAttributes.Descriptor d in attributes)
			{
				if (d.DeclaredIndex == ProbeAttribute)
				{
					foundProbe = true;
					check(d.Name == Owner.Segment + "." + AttributeLeaf && !d.FirstParty
							&& d.Arity == 1 && d.Type == ExtScalarType.Float32,
						"the self-test's own attribute enumerates with its registered name, type "
						+ "and arity, and is NOT flagged first-party (" + d.Name + ")");
					// Oxygen was written, overwritten and cleared above, so this attribute is at
					// valueCount 0 with a non-zero write count -- which is exactly the pair the
					// descriptor carries `writes` for. A client seeing 0 values cannot otherwise
					// tell an attribute nobody has pushed from one whose pusher cleared it.
					check(d.ValueCount == 0 && d.Writes >= 3,
						"and it reports 0 elements with a value but " + d.Writes + " writes -- "
						+ "the pusher ran and cleared, which reads identically to \"nobody "
						+ "pushed\" in every field except this one");
					check(SimExtElementAttributes.Keys(d.DeclaredIndex).Count == 0,
						"its key list is empty, agreeing with valueCount");
				}
				else if (d.Name == SimExtElementAttributes.MolecularMassAttribute)
				{
					foundMolecular = true;
					check(d.FirstParty,
						"sim.molecular_mass enumerates as FIRST-PARTY -- the sim registered it, "
						+ "and the \"sim.\" owner is reserved so no mod could have");
					List<SimHashes> keys = SimExtElementAttributes.Keys(d.DeclaredIndex);
					check(keys.Count == d.ValueCount,
						"its key list is exactly as long as valueCount (" + keys.Count + ")");
					check(keys.Contains(SimHashes.Oxygen),
						"and Oxygen is in it, which is how a tool DISCOVERS that this element "
						+ "carries a molar-mass override without being told to ask about it");
					bool ascending = true;
					for (int i = 1; i < keys.Count; i++)
					{
						if ((int)keys[i] <= (int)keys[i - 1])
						{
							ascending = false;
						}
					}
					check(ascending, "the keys come back ascending by id, so a client diffing two "
						+ "of these does not have to sort first");
				}
			}
			check(foundProbe && foundMolecular,
				"enumeration reached both the mod attribute and the sim's own");

			SimExtElementAttributes.Descriptor missing;
			check(!SimExtElementAttributes.TryDescribe(-1, out missing)
					&& !SimExtElementAttributes.TryDescribe(attributes.Count, out missing),
				"describing out of range, both ends, is refused rather than answered");
			check(SimExtElementAttributes.Keys(attributes.Count).Count == 0,
				"and the keys of an unregistered index are empty, which is the same answer as "
				+ "an attribute nobody has written -- both mean there is nothing to read");

			// -------------------------------------------------- registry 3: event streams
			check(refusalStreamFound,
				"the sim.message_refused stream is declared and was subscribed");
			SimExtFrame.PublishedStream stream;
			bool streamFound = SimExtFrame.TryGetStream(SimExtEventStreams.MessageRefusedStream,
				out stream);
			check(streamFound, "the subscribed stream appears in the published stream table");
			if (streamFound)
			{
				check(stream.Stride == SimExtEventStreams.RefusedRecordBytes,
					"its record stride is the 16 bytes this build decodes (" + stream.Stride + ")");
				check(stream.Dropped == 0,
					"nothing was dropped to the per-frame cap, so the records are the whole story");
			}

			// EVERY record is logged, not only the one that was provoked. A diagnostic stream
			// that reports its own test case and quietly swallows the rest would be worse than
			// none: the interesting record is always the one nobody expected.
			bool sawOurRefusal = false;
			for (int i = 0; i < refusals.Count; i++)
			{
				log("refusal record: " + refusals[i]);
				if (refusals[i].MessageId == OniExtMessages.SetCellProperty
					&& refusals[i].Reason == ExtRefusalReason.BadTarget)
				{
					sawOurRefusal = true;
				}
			}
			check(sawOurRefusal,
				"the deliberately bad write came back as a BadTarget refusal record -- a dropped "
				+ "message is now reportable instead of silent (" + refusals.Count
				+ " record(s) collected)");

			// -------------------------------------------------- the published phase list
			//
			// The native side of this is asserted by `driver/src/vftest.cpp` against a list of
			// all 21 names typed out by hand, and that guard is not duplicated here: a THIRD
			// hand-typed copy would make every phase-list edit a three-file change, two of them
			// drift guards, while catching nothing vftest does not already fail on. What only a
			// live run can catch is the MARSHALLING -- a descriptor read at the wrong stride, a
			// name decoded past its 32-byte field, the scope and gate int32s swapped -- so the
			// checks below are the ones that fail when the bytes are read wrongly, plus the
			// anchors that would make a silently-empty table look like a pass.
			//
			// The whole table is logged so a reader can put it beside vftest's transcript.
			log(SimExtPhases.Summary());

			check(SimExtPhases.Available,
				"this SimDLL publishes a phase table (SIM_ExtPhaseCount is exported)");

			SimExtPhases.Descriptor[] phases = SimExtPhases.All();
			check(phases.Length == SimExtPhases.Count && phases.Length > 0,
				"the enumeration and the count agree (" + phases.Length + " phases)");

			bool indexEchoed = true, namesReal = true, namesDistinct = true, valuesLegal = true;
			for (int i = 0; i < phases.Length; i++)
			{
				if (phases[i].Index != i)
				{
					indexEchoed = false;
				}
				if (string.IsNullOrEmpty(phases[i].Name))
				{
					namesReal = false;
				}
				for (int j = 0; j < i; j++)
				{
					if (phases[j].Name == phases[i].Name)
					{
						namesDistinct = false;
					}
				}
				if (phases[i].Scope < ExtPhaseScope.Frame
					|| phases[i].Scope > ExtPhaseScope.GridInRegion
					|| (phases[i].Gate != ExtPhaseGate.None
						&& phases[i].Gate != ExtPhaseGate.Substep
						&& phases[i].Gate != ExtPhaseGate.World))
				{
					valuesLegal = false;
				}
			}
			check(indexEchoed,
				"every descriptor echoes its own index -- the cheapest detector of a descriptor "
				+ "read at the wrong stride, since a shifted read desynchronises immediately");
			check(namesReal && namesDistinct,
				"every phase has a non-empty name and no two share one");
			check(valuesLegal,
				"every scope and gate is a value the ABI defines -- scope and gate are adjacent "
				+ "int32s, and reading them the wrong way round produces a gate of 2, which is "
				+ "not a legal bitmask");

			SimExtPhases.Descriptor drain, project, zeroMassless, conduction, diseaseDiffusion;
			check(SimExtPhases.TryDescribe(0, out drain) && drain.Name == "DrainQueue"
					&& drain.Scope == ExtPhaseScope.Frame && drain.Gate == ExtPhaseGate.None,
				"phase 0 is DrainQueue, once per frame and never skipped -- the message queue "
				+ "drains ahead of the physics that reads it");
			check(SimExtPhases.TryDescribe(phases.Length - 1, out project)
					&& project.Name == "Project" && project.Scope == ExtPhaseScope.Frame
					&& project.Gate == ExtPhaseGate.None,
				"the last phase is Project, once per frame and never skipped -- the publish every "
				+ "read window is a view onto");
			check(SimExtPhases.TryGetByName("ZeroMasslessCells", out zeroMassless)
					&& zeroMassless.Scope == ExtPhaseScope.GridInRegion,
				"ZeroMasslessCells is declared whole-grid-inside-the-region-loop, the one scope "
				+ "no single-region world can distinguish from per-region");
			check(SimExtPhases.TryGetByName("StepConduction", out conduction)
					&& conduction.MayBeSkipped && !conduction.SkippedByWorldState,
				"StepConduction is substep-gated but not world-gated: it is skipped on a frame "
				+ "that runs no substeps, and by nothing else");
			check(SimExtPhases.TryGetByName("StepDiseaseDiffusion", out diseaseDiffusion)
					&& diseaseDiffusion.SkippedByWorldState,
				"StepDiseaseDiffusion is world-gated -- a world with disease switched off never "
				+ "runs it, and 'it did not run' is a normal outcome");

			check(SimExtPhases.RunsBefore("DrainQueue", "Project")
					&& !SimExtPhases.RunsBefore("Project", "DrainQueue"),
				"RunsBefore orders two real phases and is not symmetric");
			check(!SimExtPhases.RunsBefore("DrainQueue", "NoSuchPhase")
					&& SimExtPhases.IndexOf("NoSuchPhase") < 0,
				"an unpublished phase name resolves to -1 and orders against nothing, rather "
				+ "than to a plausible index");

			SimExtPhases.Descriptor refused;
			check(!SimExtPhases.TryDescribe(-1, out refused)
					&& !SimExtPhases.TryDescribe(phases.Length, out refused)
					&& refused.Name == null && refused.Index == 0,
				"both ends are refused and the descriptor is left at its default, so a caller "
				+ "that ignores the return value does not find a plausible phase in it");

			// The claim only OnLoad could test: the table is compile-time, not world state.
			check(phaseCountAtLoad == phases.Length
					&& phaseTableAtLoad == JoinPhaseNames(phases),
				"the phase table read at OnLoad -- before SIM_Initialize, with no world at all "
				+ "-- is identical to the one read now, thousands of ticks into a world ("
				+ phaseCountAtLoad + " phases then, " + phases.Length + " now)");

			// -------------------------------------------------- the message surface
			//
			// The native side of this is asserted by `driver/src/gastest.cpp` against a list of
			// all 23 ids typed out by hand, and that guard is not duplicated here for the same
			// reason the phase list's is not. What only a live run can catch is the MARSHALLING
			// -- a 60-byte descriptor read at the wrong stride, a name decoded past its 40-byte
			// field, the adjacent `messageClass` and `delivery` int32s swapped -- plus one thing
			// no native suite can see at all, which is the cross-check at the end of this block.
			log(SimExtMessages.Summary());

			check(SimExtMessages.Available,
				"this SimDLL publishes a message table (SIM_ExtMessageCount is exported)");

			SimExtMessages.Descriptor[] messages = SimExtMessages.All();
			check(messages.Length == SimExtMessages.Count && messages.Length > 0,
				"the enumeration and the count agree (" + messages.Length + " messages)");

			bool msgIndexEchoed = true, msgNamesReal = true, msgNamesDistinct = true;
			bool msgNamesPrefixed = true, msgIdsDistinct = true, msgValuesLegal = true;
			int minId = int.MaxValue, maxId = int.MinValue;
			int queued = 0, immediate = 0;
			int parameters = 0, operations = 0, stores = 0, checkpoints = 0;
			for (int i = 0; i < messages.Length; i++)
			{
				SimExtMessages.Descriptor m = messages[i];
				if (m.Index != i)
				{
					msgIndexEchoed = false;
				}
				if (string.IsNullOrEmpty(m.Name))
				{
					msgNamesReal = false;
				}
				else if (m.Name[0] != 'k')
				{
					msgNamesPrefixed = false;
				}
				for (int j = 0; j < i; j++)
				{
					if (messages[j].Name == m.Name)
					{
						msgNamesDistinct = false;
					}
					if (messages[j].Id == m.Id)
					{
						msgIdsDistinct = false;
					}
				}
				if (m.Id < minId)
				{
					minId = m.Id;
				}
				if (m.Id > maxId)
				{
					maxId = m.Id;
				}
				if (m.Class < ExtMessageClass.Parameter || m.Class > ExtMessageClass.Checkpoint
					|| (m.Delivery != ExtMessageDelivery.Queued
						&& m.Delivery != ExtMessageDelivery.Immediate)
					|| m.Phase < SimExtMessages.PhaseUnpublished
					|| m.Phase >= phases.Length)
				{
					msgValuesLegal = false;
				}

				if (m.Delivery == ExtMessageDelivery.Immediate)
				{
					immediate++;
				}
				else
				{
					queued++;
				}
				switch (m.Class)
				{
					case ExtMessageClass.Parameter: parameters++; break;
					case ExtMessageClass.Operation: operations++; break;
					case ExtMessageClass.Store: stores++; break;
					case ExtMessageClass.Checkpoint: checkpoints++; break;
				}
			}

			check(msgIndexEchoed,
				"every descriptor echoes its own index -- the cheapest detector of a 60-byte "
				+ "descriptor read at the wrong stride, since a shifted read desynchronises "
				+ "immediately");
			check(msgNamesReal && msgNamesDistinct,
				"every message has a non-empty name and no two share one");
			check(msgNamesPrefixed,
				"every published name is the k-prefixed C++ constant, not the ONI_MSG_ spelling "
				+ "of the C header nor the unprefixed managed one -- the cross-check below "
				+ "depends on knowing which of the three this is");
			check(msgIdsDistinct,
				"no two messages share a wire id");
			check(msgValuesLegal,
				"every class, delivery and phase is a value the ABI defines -- messageClass and "
				+ "delivery are ADJACENT int32s, and reading them the wrong way round gives a "
				+ "delivery of 2 or 3 for the twelve store and checkpoint rows, which is not a "
				+ "legal delivery");
			// "ONIR" and "ONIT" are deliberately skipped ids, claimed by work that has not landed
			// (OniExtMessages.cs says so at each). The native static_assert allows exactly those
			// (kExtMessageReservedIds), so a gap is a failure only when it is not one of them.
			int[] reservedIds = { 0x4F4E4952, 0x4F4E4954 };
			int reservedInRange = 0;
			bool reservedUnused = true;
			foreach (int r in reservedIds)
			{
				if (r >= minId && r <= maxId)
				{
					reservedInRange++;
				}
				foreach (SimExtMessages.Descriptor m in messages)
				{
					reservedUnused &= m.Id != r;
				}
			}
			check(reservedUnused && maxId - minId + 1 == messages.Length + reservedInRange,
				"the ids are contiguous over their range apart from the " + reservedIds.Length
				+ " reserved ones (0x" + minId.ToString("X8") + "..0x" + maxId.ToString("X8")
				+ " for " + messages.Length + " rows + " + reservedInRange + " reserved"
				+ (reservedUnused ? "" : ", AND A RESERVED ID HAS A ROW") + ") -- the same property the native static_assert holds, and the reason a missing row is "
				+ "catchable at all");

			// The censuses the ABI header states as prose. They are worth asserting because the
			// headline number overstates the surface: only Parameter and Operation extend what
			// the simulation does.
			// THESE COUNTS ARE MAINTAINED BY HAND ON PURPOSE. A new message makes two arms fail,
			// which is what a correct change to the message table is SUPPOSED to look like from
			// here; a census updated automatically to whatever the DLL says would pass forever.
			//
			// kSetTunable (ONIV) is an immediate Parameter: it sets the sim's tunable table, and
			// it is immediate because SubstepSeconds may change only while no world exists, when
			// no frame runs to drain a queue.
			//
			// kSetVisibilityState (ONIW) is an immediate Checkpoint: plumbing, the
			// ninth component a replay restores, immediate for the reason kSetCellRadiation is (the
			// next frame's call writes a buffer it restores). Hence 29/8 and 17/3/9/8.
			//
			// kSetLoadIsRestore (ONIX) is an immediate Checkpoint too: plumbing, sent
			// before a restore's Load so that Load skips the load-time transition, and immediate
			// because a queued one would drain after the Load it was for. Hence 29/9 and 17/3/9/9.
			//
			// So: when one of these fails, look at WHICH class moved and why before editing the
			// number. A new Parameter or Operation is a new thing the simulation can be asked to
			// do and belongs in the API docs; a new Store or Checkpoint is plumbing. Editing the
			// count without that reading turns the assertion into a mirror.
			check(queued == 29 && immediate == 9,
				"29 queued / 9 immediate (" + queued + " / " + immediate + ") -- delivery is the "
				+ "one field a caller's code SHAPE depends on, so a change here is a change to "
				+ "every consumer");
			check(parameters == 17 && operations == 3 && stores == 9 && checkpoints == 9,
				"17 parameter / 3 operation / 9 store / 9 checkpoint (" + parameters + " / "
				+ operations + " / " + stores + " / " + checkpoints + ") -- "
				+ (parameters + operations) + " of the " + messages.Length + " ids are extension "
				+ "points and the other " + (stores + checkpoints) + " are not");

			// Spot checks that JOIN THE TWO FACADES. A message's phase is an index into the phase
			// table and nothing else resolves it, so this is the assertion that the pair works
			// together rather than merely both existing.
			SimExtMessages.Descriptor bonus, molarMass, registerProperty, randomState;
			check(SimExtMessages.TryGetById(OniExtMessages.SetCellThermalMassBonus, out bonus)
					&& bonus.Name == "kSetCellThermalMassBonus"
					&& bonus.Class == ExtMessageClass.Parameter
					&& bonus.Delivery == ExtMessageDelivery.Queued
					&& SimExtMessages.PhaseName(bonus) == "StepConduction",
				"kSetCellThermalMassBonus is a queued parameter read by StepConduction -- its "
				+ "phase index resolved through SimExtPhases, which is the whole reason the two "
				+ "tables ship together");
			check(SimExtMessages.TryGetById(OniExtMessages.SetMolecularMass, out molarMass)
					&& molarMass.Delivery == ExtMessageDelivery.Immediate
					&& !molarMass.HasPublishedPhase
					&& SimExtMessages.PhaseName(molarMass) == null,
				"kSetMolecularMass is IMMEDIATE and has no published phase -- it was made "
				+ "immediate because one tick of latency was a real race against exports that "
				+ "take no worker barrier, and an unpublished phase resolves to null rather than "
				+ "to phase 0");
			check(SimExtMessages.TryGetById(OniExtMessages.RegisterCellProperty,
						out registerProperty)
					&& registerProperty.Delivery == ExtMessageDelivery.Immediate
					&& registerProperty.Class == ExtMessageClass.Store
					&& !registerProperty.IsExtensionPoint,
				"kRegisterCellProperty is an immediate Store -- it HAS to be immediate, because "
				+ "registration closes at the first SIM_AllocateCells and a queued one would "
				+ "drain after the array it wanted to declare had been sized");
			check(SimExtMessages.TryGetById(OniExtMessages.SetRandomState, out randomState)
					&& randomState.Class == ExtMessageClass.Checkpoint
					&& !randomState.IsExtensionPoint,
				"kSetRandomState is a Checkpoint and reports itself as NOT an extension point -- "
				+ "it restores run state a save blob cannot carry");

			// Delivery(wireId): the one question a caller actually has, and the answer that has
			// to distinguish "unknown" from "queued".
			check(SimExtMessages.Delivery(OniExtMessages.SetMolecularMass)
						== ExtMessageDelivery.Immediate
					&& SimExtMessages.Delivery(OniExtMessages.SetCellThermalMassBonus)
						== ExtMessageDelivery.Queued,
				"Delivery() answers on the WIRE ID, which is what a caller holds -- an "
				+ "OniExtMessages constant is an id, never a table position");
			check(SimExtMessages.Delivery(0) == null
					&& SimExtMessages.PhaseName(0) == null
					&& SimExtMessages.IndexOfId(0) < 0,
				"an id this DLL does not implement comes back null, DISTINCTLY from Queued -- "
				+ "queued is a promise the write lands at the next drain, and an unrecognised id "
				+ "is dropped, so spelling both as the zero-valued enum would promise delivery "
				+ "of a message that will never arrive");

			SimExtMessages.Descriptor refusedMsg;
			check(!SimExtMessages.TryDescribe(-1, out refusedMsg)
					&& !SimExtMessages.TryDescribe(messages.Length, out refusedMsg)
					&& refusedMsg.Name == null && refusedMsg.Id == 0,
				"both ends are refused and the descriptor is left at its default, so a caller "
				+ "that ignores the return value does not find a plausible message in it");

			// The claim only OnLoad could test: the table is compile-time, not world state.
			check(messageCountAtLoad == messages.Length
					&& messageTableAtLoad == JoinMessageNames(messages),
				"the message table read at OnLoad -- before SIM_Initialize, with no world at all "
				+ "-- is identical to the one read now, thousands of ticks into a world ("
				+ messageCountAtLoad + " messages then, " + messages.Length + " now)");

			// -------------------------------------------------- the third copy, finally checked
			//
			// OniExtMessages is a hand-maintained THIRD copy of these ids, and its own header
			// comment says it is checked by nothing -- which is exactly how kSetExtCellState went
			// missing from it for two days while the two native copies checked each other at
			// compile time. This is the first mechanism that can check it, and it checks it
			// against the DLL that will actually dispatch the message rather than against another
			// list somebody typed.
			//
			// Enumerated BY REFLECTION on purpose. A hand-typed list here would be a FOURTH copy
			// and would prove nothing -- the same argument gastest.cpp makes in the other
			// direction, where the list is typed out by hand precisely because a check generated
			// from its own subject passes by construction. Here the subject is OniExtMessages and
			// the independent authority is the DLL, so reflection is the honest enumeration.
			List<string> managedNames = new List<string>();
			List<int> managedIds = new List<int>();
			FieldInfo[] constants = typeof(OniExtMessages).GetFields(
				BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
			for (int i = 0; i < constants.Length; i++)
			{
				if (!constants[i].IsLiteral || constants[i].FieldType != typeof(int))
				{
					continue;
				}
				managedNames.Add(constants[i].Name);
				managedIds.Add((int)constants[i].GetRawConstantValue());
			}

			check(managedNames.Count == messages.Length,
				"OniExtMessages holds exactly as many ids as the DLL publishes ("
				+ managedNames.Count + " managed constants, " + messages.Length
				+ " published) -- counting is what found kSetExtCellState missing, at 22 against "
				+ "23");

			List<string> unpublished = new List<string>();
			for (int i = 0; i < managedNames.Count; i++)
			{
				SimExtMessages.Descriptor publishedMsg;
				if (!SimExtMessages.TryGetById(managedIds[i], out publishedMsg)
					|| publishedMsg.Name != "k" + managedNames[i])
				{
					unpublished.Add(managedNames[i] + " (0x" + managedIds[i].ToString("X8") + ")");
				}
			}
			check(unpublished.Count == 0,
				"every OniExtMessages constant is implemented by this DLL under the same id and "
				+ "the k-prefixed spelling of the same name"
				+ (unpublished.Count == 0
					? ""
					: " -- MISSING OR RENAMED: " + string.Join(", ", unpublished.ToArray())));

			List<string> unmirrored = new List<string>();
			for (int i = 0; i < messages.Length; i++)
			{
				bool mirrored = false;
				for (int j = 0; j < managedIds.Count; j++)
				{
					if (managedIds[j] == messages[i].Id
						&& "k" + managedNames[j] == messages[i].Name)
					{
						mirrored = true;
						break;
					}
				}
				if (!mirrored)
				{
					unmirrored.Add(messages[i].Name + " (0x" + messages[i].Id.ToString("X8") + ")");
				}
			}
			check(unmirrored.Count == 0,
				"and the other direction: every id this DLL publishes has a managed constant, so "
				+ "a message added natively and forgotten here is a FAILURE rather than an "
				+ "absence nobody counts"
				+ (unmirrored.Count == 0
					? ""
					: " -- NOT MIRRORED: " + string.Join(", ", unmirrored.ToArray())));

			// -------------------------------------------------- the room graph (SimRooms)
			//
			// The live half of the room-graph checks. vftest covers the same two exports offline;
			// what only a live process can
			// show is the managed decode -- a field read at the wrong offset, a fixed buffer
			// filled from the wrong stride -- and whether the aggregate agrees with the per-cell
			// reads it is meant to replace on a real world rather than a 5x1 test line.
			//
			// Provoke injected a quarter kilogram into one gas cell precisely so there is a room
			// graph to read; see the comment there for why that is necessary and what it costs.

			log("room graph: " + (SimRooms.Available ? "available" : "NOT available")
				+ ", probe cell " + roomProbeCell + " species " + roomProbeSpecies);

			check(SimRooms.Available,
				"this SimDLL publishes the room graph (SIM_RoomId / SIM_RoomAggregate are "
				+ "exported) -- the same custom-DLL precondition every other arm here has");
			check(roomProbeCell >= 0,
				"the canvas held a gas cell to inject into, so the room graph could be brought "
				+ "into existence at all -- a canvas of pure vacuum would make every arm below "
				+ "vacuously true, which is why this is asserted rather than assumed");

			int roomId = SimRooms.RoomId(roomProbeCell);
			check(roomId != SimRooms.NoRoom,
				"the probe cell is in a real room now -- the room graph does not exist until the "
				+ "mixture layer is activated, so this failing would mean the injection never "
				+ "landed rather than that the export is broken");

			SimRooms.RoomAggregate roomAgg;
			bool roomFilled = SimRooms.TryGetAggregate(roomProbeCell, out roomAgg);
			check(roomFilled, "the aggregate is filled for a cell that is in a room");
			log("room aggregate: " + roomAgg.ToString());

			check(roomAgg.RoomId == roomId,
				"the aggregate names the same room SIM_RoomId does -- two exports over one graph, "
				+ "not two graphs");
			check(roomAgg.CellCount > 1,
				"the room is a real component of the world rather than the single cell that was "
				+ "injected into -- a per-cell answer dressed up as a room would report 1");

			// THE CROSS-CHECK THAT MATTERS, and it is deliberately run through the OLD shape.
			// Every number in the aggregate is re-derived here from the per-cell exports a
			// consumer uses today -- the allocating GasMixtureFacade.TryGetComposition among them
			// -- because the risk being tested is not "does the native sum work" (vftest answers
			// that) but "does the one-call answer differ from the many-call answer a caller is
			// switching away from". A silent difference here is a mod's behaviour changing on an
			// upgrade for reasons nobody can see.
			int roomCellsSeen = 0;
			int roomMixtureCells = 0;
			double roomMixtureMass = 0.0;
			double roomVanillaMass = 0.0;
			double roomPressureSum = 0.0;
			double roomProbeSpeciesMass = 0.0;
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (SimRooms.RoomId(cell) != roomId)
				{
					continue;
				}
				roomCellsSeen++;
				roomVanillaMass += Grid.Mass[cell];
				GasMixtureFacade.GasComponent[] composition =
					GasMixtureFacade.TryGetComposition(cell);
				if (composition.Length > 0)
				{
					roomMixtureCells++;
				}
				for (int i = 0; i < composition.Length; i++)
				{
					roomMixtureMass += composition[i].MassKg;
					if (composition[i].ElementIdx == roomProbeSpecies)
					{
						roomProbeSpeciesMass += composition[i].MassKg;
					}
				}
				float cellPressurePa;
				if (GasMixtureFacade.TryGetPressure(cell, out cellPressurePa))
				{
					roomPressureSum += cellPressurePa;
				}
			}
			log("re-derived per cell: " + roomCellsSeen + " cells with this room id, "
				+ roomMixtureCells + " with mixture, " + roomMixtureMass.ToString("F6")
				+ " kg mixture, " + roomVanillaMass.ToString("F4") + " kg vanilla, mean "
				+ (roomMixtureCells > 0 ? roomPressureSum / roomMixtureCells : 0.0).ToString("F4")
				+ " Pa");

			check(roomCellsSeen == roomAgg.CellCount,
				"the aggregate's cell count is the number of cells that actually report this room "
				+ "id, counted one at a time over the whole grid");
			check(roomMixtureCells == roomAgg.MixtureCellCount,
				"mixtureCellCount is exactly the cells that answer a composition, which is what "
				+ "makes it the honest denominator for the mean pressure");
			check(Math.Abs(roomMixtureMass - roomAgg.MixtureMassKg) < 1e-3,
				"the room's mixture mass equals the sum of its cells' own compositions -- the one "
				+ "call and the many calls agree");
			check(Math.Abs(roomAgg.MixtureMassKg - RoomProbeMassKg) < 1e-2,
				"and that sum is the " + RoomProbeMassKg + " kg injected -- mixing spread it "
				+ "across the room without creating or destroying any of it");
			check(roomAgg.MixtureCellCount > 0
					&& Math.Abs(roomPressureSum / roomAgg.MixtureCellCount - roomAgg.MeanPressurePa)
						< 0.5,
				"the mean pressure is the mean of those cells' own SIM_GasPressure answers");
			check(roomAgg.VanillaMassKg > 0f
					&& Math.Abs(roomVanillaMass - roomAgg.VanillaMassKg)
						< 0.02 * Math.Max(1.0, roomVanillaMass),
				"vanillaMassKg is the OTHER layer -- the PhaseEntry mass the room holds, agreeing "
				+ "with Grid.Mass summed over the same cells, and reported separately rather than "
				+ "folded into the mixture total");
			check(roomAgg.VanillaMassKg > roomAgg.MixtureMassKg,
				"and on this canvas the two are plainly different numbers, so an aggregate that "
				+ "reported one of them twice could not pass");
			check(roomAgg.TemperatureK > 1f,
				"the mass-weighted temperature is a real temperature, not the zero an unweighted "
				+ "mean over massless cells would give");

			check(roomAgg.SpeciesCount == 1 && roomAgg.SpeciesAt(0) == roomProbeSpecies,
				"the composition holds exactly the one species injected, by its ElementTable "
				+ "index -- counted once for the room rather than once per cell");
			check(roomAgg.SpeciesOverflow == 0,
				"one species cannot overflow sixteen slots, so nothing is unattributed");
			float roomSpeciesMassKg;
			check(roomAgg.TryGetMassKg((ushort)roomProbeSpecies, out roomSpeciesMassKg)
					&& Math.Abs(roomSpeciesMassKg - roomProbeSpeciesMass) < 1e-3,
				"that species' room total equals its per-cell sum, looked up by element index "
				+ "rather than by slot position");
			float roomAbsentMassKg;
			check(!roomAgg.TryGetMassKg(ushort.MaxValue, out roomAbsentMassKg)
					&& roomAbsentMassKg == 0f,
				"a species the room does not hold is refused rather than answered with a zero a "
				+ "caller could not tell from a real one");

			check(roomAgg.Owned == GasMixtureFacade.IsRoomOwned(roomProbeCell),
				"the aggregate's promotion flag is the same bit SIM_DebugRoomOwned reports for "
				+ "the same cell -- the old boolean export and the new struct read one field");
			check(!roomAgg.Owned,
				"and it is false, because nothing promoted this room: an unpromoted room is a "
				+ "real, readable room, which is the property that makes this readable on a "
				+ "world Mod 1 has not touched");
			check(roomAgg.AwakeCount >= 0 && roomAgg.AwakeCount <= roomAgg.CellCount,
				"awakeCount is a subset of the room, never larger than the room it is counted in");

			// ONE ROOM, ONE ANSWER. This is the property that lets a caller group by room id and
			// ask once, and it is exactly what a radius-N diamond cannot give: two vents in one
			// room would read two different "rooms" and disagree about their own atmosphere.
			int roomFarCell = -1;
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (cell != roomProbeCell && SimRooms.RoomId(cell) == roomId)
				{
					roomFarCell = cell;
					break;
				}
			}
			SimRooms.RoomAggregate roomFromFar = default(SimRooms.RoomAggregate);
			bool roomFarFilled = roomFarCell >= 0
				&& SimRooms.TryGetAggregate(roomFarCell, out roomFromFar);
			check(roomFarFilled, "a second cell of the same room also answers with an aggregate");
			check(roomFarFilled && RoomAggregatesMatch(roomAgg, roomFromFar),
				"every cell of a room answers with an IDENTICAL aggregate, field for field and "
				+ "slot for slot -- one room, one answer, from cell " + roomProbeCell + " and "
				+ "cell " + roomFarCell);

			// Refusals. The contract is that a caller which ignores the return value cannot read a
			// plausible room out of the struct, so the failure path has to WRITE, not just return.
			check(SimRooms.RoomId(-1) == SimRooms.NoRoom
					&& SimRooms.RoomId(Grid.CellCount + 1000) == SimRooms.NoRoom,
				"both ends of the cell range report the no-room sentinel rather than indexing "
				+ "something");
			SimRooms.RoomAggregate roomRefused;
			bool roomRefusedFilled = SimRooms.TryGetAggregate(-1, out roomRefused);
			check(!roomRefusedFilled && roomRefused.RoomId == SimRooms.NoRoom
					&& roomRefused.CellCount == 0 && roomRefused.SpeciesCount == 0
					&& roomRefused.MixtureMassKg == 0f && roomRefused.VanillaMassKg == 0f,
				"an invalid cell is refused AND the aggregate is written with the no-room "
				+ "sentinel and zeros, so a caller that ignores the bool finds nothing plausible "
				+ "in it");
			check(roomRefused.SpeciesAt(0) == 0 && roomRefused.MassKgAt(0) == 0f
					&& roomRefused.ToString() == "no room",
				"and its composition accessors are bounded by SpeciesCount rather than reading "
				+ "the buffer regardless, so slot 0 of a refused aggregate is empty");
			check(!SimRooms.HasRoom(-1) && SimRooms.HasRoom(roomProbeCell),
				"HasRoom answers the same question as the sentinel comparison, both ways round");

			// -------------------------------------------------- the grid raycast (SimRaycast)
			//
			// The live half of the raycast checks. gastest checks the walk against Klei's own
			// RadiationAbsorptionAlongLine over thousands of rays and vftest checks the export's
			// cell indexing and refusals; what only a live process can show is the managed decode
			// of the two explicit-layout structs, and whether the sim's answer agrees with
			// vanilla's own Grid on a real world. A HORIZONTAL ray is used on purpose: its cells
			// are consecutive game cells, so the expected answer can be read off Grid.Element
			// with no second line algorithm to disagree with.
			int rayStart = roomProbeCell >= 0 ? roomProbeCell : 0;
			int rayEnd = rayStart;
			for (int i = 0; i < 24 && roomProbeCell >= 0 && Grid.IsValidCell(Grid.CellRight(rayEnd))
				&& Grid.CellRow(Grid.CellRight(rayEnd)) == Grid.CellRow(rayStart); i++)
			{
				rayEnd = Grid.CellRight(rayEnd);
			}
			int expectHit = SimRaycast.NoCell;
			for (int c = rayStart; c <= rayEnd; c++)
			{
				if (Grid.Element[c].IsSolid)
				{
					expectHit = c;
					break;
				}
			}
			log("raycast: " + (SimRaycast.Available ? "available" : "NOT available") + ", row ray "
				+ rayStart + " -> " + rayEnd + ", vanilla's Grid says the first solid is "
				+ expectHit);
			SimRaycast.Hit rayHit;
			bool rayCast = SimRaycast.TryCast(rayStart, rayEnd, SimRaycast.Phase.Solid, out rayHit);
			log("raycast: " + rayHit);
			check(SimRaycast.Available && rayCast && rayEnd > rayStart,
				"this SimDLL casts rays (SIM_QueryRaycast is exported) and the probe row "
				+ "gave the ray somewhere to go");
			check(rayHit.HitCell == expectHit
					&& rayHit.LastClearCell == (expectHit == SimRaycast.NoCell ? rayEnd
						: (expectHit == rayStart ? SimRaycast.NoCell : expectHit - 1))
					&& rayHit.CellsVisited == (expectHit == SimRaycast.NoCell ? rayEnd - rayStart + 1
						: expectHit - rayStart + 1),
				"the sim's first solid cell along the row, the cell before it and the count "
				+ "visited are the ones vanilla's own Grid.Element reports -- the decode is at the "
				+ "right offsets and the cells are game cells");

			// A CLEAR RAY NEEDS A CLEAR LINE, and the probe row is not one: its last cell IS the
			// first solid, so a Phase.None ray over the whole row passes THROUGH a tile -- and
			// Klei's product over a tile is zero. `Phase.None` means nothing STOPS the ray, not
			// that nothing absorbs it; sim/raycast.h says so in as many words ("it still
			// attenuates it"), so reading `transmission > 0` as "nothing stopped it, so something
			// survived" is wrong: gastest asserts offline, over random lines, that this float IS
			// RadiationAbsorptionAlongLine's, bit for bit, and over a tile that is zero.
			//
			// So the claim splits in two, and neither half is true by construction: over the
			// cells BEFORE the first solid a ray keeps something, and running that same ray on
			// through the tile keeps strictly less. Together that is what "it attenuates" means,
			// and a sim that returned a constant would fail both.
			int rayClearEnd = expectHit == SimRaycast.NoCell ? rayEnd
				: (expectHit > rayStart ? expectHit - 1 : rayStart);
			SimRaycast.Query[] rayQueries = new SimRaycast.Query[]
			{
				new SimRaycast.Query(rayStart, rayEnd, SimRaycast.Phase.None),
				new SimRaycast.Query(rayEnd, rayStart, SimRaycast.Phase.None),
				new SimRaycast.Query(-1, rayEnd, SimRaycast.Phase.Solid),
				new SimRaycast.Query(rayStart, rayClearEnd, SimRaycast.Phase.None),
			};
			SimRaycast.Hit[] rayResults = new SimRaycast.Hit[4];
			for (int i = 0; i < rayResults.Length; i++)
			{
				rayResults[i].HitCell = 12345;
				rayResults[i].CellsVisited = 12345;
			}
			int rayAnswered = SimRaycast.CastBatch(rayQueries, rayResults, 4);
			// PRINT THE BATCH BEFORE ASSERTING ON IT. The three checks below read eleven fields
			// between them, and a failure must say WHICH conjunct went without a rebuild. The single-cast arm above
			// already logs its Hit; these are the same line for the rest of the batch.
			log("raycast whole row: " + rayResults[0]);
			log("raycast reverse: " + rayResults[1]);
			log("raycast refused: " + rayResults[2]);
			log("raycast clear segment " + rayStart + " -> " + rayClearEnd + ": " + rayResults[3]);
			check(rayAnswered == 3,
				"a batch of four with one invalid query answers the three valid ones and says so");
			check(!rayResults[0].Stopped && rayResults[0].LastClearCell == rayEnd
					&& rayResults[0].CellsVisited == rayEnd - rayStart + 1
					&& rayResults[0].Transmission >= 0f && rayResults[0].Transmission <= 1f,
				"with nothing to stop it the ray runs the whole row -- every cell visited, no "
				+ "stopping cell -- and keeps a transmission in [0, 1]");
			check(rayClearEnd > rayStart
					&& !rayResults[3].Stopped
					&& rayResults[3].LastClearCell == rayClearEnd
					&& rayResults[3].CellsVisited == rayClearEnd - rayStart + 1
					&& rayResults[3].Transmission > 0f && rayResults[3].Transmission <= 1f
					&& rayResults[0].Transmission <= rayResults[3].Transmission,
				"a ray over the clear cells before the first solid keeps a transmission in "
				+ "(0, 1] (" + rayResults[3].Transmission.ToString("G6") + " over "
				+ rayResults[3].CellsVisited + " cells), and running it on through the tile keeps "
				+ "no more (" + rayResults[0].Transmission.ToString("G6") + ") -- Phase.None "
				+ "means nothing STOPS the ray, not that nothing absorbs it");
			check(rayResults[1].Transmission == rayResults[0].Transmission
					&& rayResults[1].LastClearCell == rayStart,
				"the same ray cast back is the same float -- Klei's walk is one walk from either "
				+ "end -- and its last clear cell is the caller's end, which is now the start");
			check(!rayResults[2].Valid && rayResults[2].HitCell == SimRaycast.NoCell
					&& rayResults[2].LastClearCell == SimRaycast.NoCell,
				"the invalid query's result is WRITTEN as a refusal over the poison, not left");

			// -------------------------------------------------- the field solver (SimFields)
			//
			// The live half of the field-solver checks. gastest asserts the two walks against the sim
			// functions they generalise and asserts both invariants with their faults planted;
			// what only a live process can show is that the message reaches a real DLL, that the
			// index comes back through SIM_HandleMessage's void*, and that the 96-byte descriptor
			// decodes at the offsets this file claims.
			//
			// THE FIELD REGISTERED HERE IS IDLE ON PURPOSE -- no attenuation, no source -- so
			// these arms cannot move a cell that the arms above are asserting exact values for.
			log("fields: " + (SimFields.Available ? "available" : "NOT available") + ", count "
				+ SimFields.Count() + ", index " + FieldIndex);
			check(SimFields.Available && FieldProperty >= 0 && FieldIndex >= 0,
				"this SimDLL has the field solver (kRegisterField and SIM_ExtField*) and "
				+ "attaching a rule to a fresh arity-1 float returned a field index");
			check(FieldDuplicateResult == -(int)ExtRegisterResult.Duplicate,
				"a SECOND rule on the same property is refused as a duplicate rather than "
				+ "silently replacing the first -- one field per property");
			check(FieldBadArityResult == -(int)ExtRegisterResult.BadProperty,
				"a rule on the arity-2 probe property is refused as a bad property: a field is "
				+ "one float per cell, and the refusal names which of the three things was wrong");
			check(SimFields.Count() >= 1 && SimFields.Find(Owner.Compose(FieldLeaf)) == FieldIndex,
				"the field is enumerable, and Find takes the PROPERTY's name -- a field has no "
				+ "name of its own, which is the design restated as a lookup");
			check(SimFields.Find(Owner.Compose(UnpublishedLeaf)) == -1,
				"and a registered property with no rule attached answers -1 rather than 0");

			SimFields.Descriptor fieldDesc;
			bool fieldDescribed = SimFields.TryDescribe(FieldIndex, out fieldDesc);
			log("fields: " + (fieldDescribed ? fieldDesc.ToString() : "describe refused"));
			check(fieldDescribed && fieldDesc.FieldIndex == FieldIndex
					&& fieldDesc.PropertyIndex == FieldProperty
					&& fieldDesc.Property == Owner.Compose(FieldLeaf)
					&& fieldDesc.AttributeIndex == SimFields.NoAttribute
					&& fieldDesc.Law == FieldAttenuationLaw.Flat
					&& fieldDesc.Combine == FieldCombine.Transmission
					&& fieldDesc.DecayMode == FieldDecay.None
					&& fieldDesc.ClampHigh == 1f,
				"the 96-byte descriptor decodes at the offsets SimFields claims: every field of "
				+ "the rule comes back as it was sent, and the property NAME comes back with it");
			check(fieldDesc.SourceCount == 0,
				"and it reports no sources -- which is the field that answers \"was somebody "
				+ "supposed to re-push after the load, and have they\", because sources are "
				+ "never saved");

			SimFields.Descriptor fieldRefused;
			check(!SimFields.TryDescribe(9999, out fieldRefused)
					&& fieldRefused.Property == null && fieldRefused.SourceCount == 0,
				"an unregistered field index is refused AND the descriptor is left at its "
				+ "default, so a caller that ignores the bool finds nothing plausible in it");

			VerifyTunables(check, log);

			// -------------------------------------------------- managed-side naming rules
			string why;
			check(!SimExtRegistry.ValidName("Selftest.Probe", out why),
				"uppercase is rejected rather than down-cased, so two spellings can never become "
				+ "one property (" + why + ")");
			check(!SimExtRegistry.ValidName("noseparator", out why),
				"a name with no owner segment is rejected (" + why + ")");
			check(SimExtRegistry.ClaimOwner("My Mod!").Segment == "my_mod",
				"a consumer name folds to a legal owner segment rather than being passed through");
		}

		// -------------------------------------------------- the tunable table (SimTunables)
		//
		// The live half of the sim's vftest tunables block. vftest proves the kernels read the table;
		// what only a live process can show is that the managed enum names the DLL's rows, that
		// the 312-byte descriptor decodes at SimTunables' offsets, that a write crosses
		// SIM_HandleMessage and its answer comes back through the void*, and that every managed
		// MIRROR of a sim number moves when the row does.
		//
		// Every write here is undone before this returns, and nothing between a write and its
		// undo yields a frame: kSetTunable is immediate and waits for the worker, so the sim never
		// steps with a probe value in the table.
		private static void VerifyTunables(Func<bool, string, bool> check, Action<string> log)
		{
			log("tunables: " + (SimTunables.Available ? SimTunables.Count + " rows" : "NOT available"));
			int members = Enum.GetValues(typeof(SimTunable)).Length;
			if (!check(SimTunables.Available && SimTunables.Count == members,
				"this SimDLL has the tunable table, with exactly as many rows as the generated "
				+ "SimTunable enum has members (" + SimTunables.Count + " / " + members + ")"))
			{
				return;
			}

			bool allKnown = true;
			foreach (SimTunable id in Enum.GetValues(typeof(SimTunable)))
			{
				SimTunableInfo info;
				if (!SimTunables.TryDescribe(id, out info) || info.Name != id.ToString()
					|| info.Id != id)
				{
					allKnown = false;
					log("tunables: " + id + " does not name its row");
				}
			}
			check(allKnown,
				"every SimTunable member names the DLL row at its id, by name -- the enum is "
				+ "generated from tunables.def, and this is the only check that the DLL loaded was "
				+ "built from the same rows");

			SimTunableInfo[] rows = SimTunables.All();
			int ceilings = 0, noWorld = 0;
			bool inRange = true, docs = true;
			foreach (SimTunableInfo r in rows)
			{
				if ((r.Flags & SimTunableFlags.Ceiling) != 0)
				{
					ceilings++;
					inRange &= r.Max == r.Stock;
				}
				if ((r.Flags & SimTunableFlags.NoWorld) != 0)
				{
					noWorld++;
				}
				inRange &= r.Min <= r.Stock && r.Stock <= r.Max && r.Min <= r.Default
					&& r.Default <= r.Max;
				docs &= !string.IsNullOrEmpty(r.Doc) && !string.IsNullOrEmpty(r.Group)
					&& !string.IsNullOrEmpty(r.Origin);
			}
			SimTunableInfo substep;
			SimTunables.TryDescribe(SimTunable.SubstepSeconds, out substep);
			check(ceilings == 8 && noWorld == 1
					&& (substep.Flags & SimTunableFlags.NoWorld) != 0,
				"8 ceilings and one no-world row, SubstepSeconds (" + ceilings + " / " + noWorld
				+ ") -- the same census vftest takes offline");
			check(inRange && docs,
				"every row's stock and default lie inside its [min, max], a ceiling's max IS its "
				+ "stock value, and every row carries a doc line, a group and an origin -- the "
				+ "descriptor's strings and doubles decode at the offsets claimed");
			SimTunable found;
			check(SimTunables.TryFind("GasPressureCap", out found)
					&& found == SimTunable.GasPressureCap
					&& !SimTunables.TryFind("NoSuchRow", out found),
				"a row is found by its exact name, and a name the table lacks is not");

			// ---- writes, refusals, and the Changed event
			ExtOwner me = Owner;
			float cap = SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN);
			float probe = cap == 0.0625f ? 0.03125f : 0.0625f;
			int changed = 0;
			Action<SimTunable> count = id =>
			{
				if (id == SimTunable.GasPressureCap)
				{
					changed++;
				}
			};
			SimTunables.Changed += count;
			SimTunableResult set = SimTunables.Set(SimTunable.GasPressureCap, probe, me);
			float cached = SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN);
			SimTunables.Refresh();
			float reread = SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN);
			SimTunableResult again = SimTunables.Set(SimTunable.GasPressureCap, probe, me);
			int changedAfterRepeat = changed;
			SimTunableResult undo = SimTunables.Set(SimTunable.GasPressureCap, cap, me);
			SimTunables.Changed -= count;
			check(set == SimTunableResult.Applied && cached == probe && reread == probe
					&& again == SimTunableResult.Applied && undo == SimTunableResult.Applied
					&& SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN) == cap,
				"a float row set through SIM_HandleMessage answers Applied, reads back from the "
				+ "DLL itself (not only the cache) as the value sent, and is restored ("
				+ cap.ToString("R") + " -> " + probe.ToString("R") + " -> "
				+ SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN).ToString("R") + ")");
			check(changedAfterRepeat == 1 && changed == 2,
				"Changed fires once per real change: once for the set, not for re-sending the "
				+ "same value, once for the undo (" + changedAfterRepeat + ", " + changed + ")");

			float maxT = SimTunables.GetFloat(SimTunable.MaxTemperature, 0f);
			float substepNow = SimTunables.GetFloat(SimTunable.SubstepSeconds, 0f);
			SimTunableResult nan = SimTunables.Set(SimTunable.GasPressureCap, float.NaN, me);
			SimTunableResult range = SimTunables.Set(SimTunable.GasPressureCap, -1f, me);
			SimTunableResult raise = SimTunables.Set(SimTunable.MaxTemperature, maxT * 2f, me);
			SimTunableResult world = SimTunables.Set(SimTunable.SubstepSeconds,
				substepNow * 0.5f, me);
			SimTunableResult same = SimTunables.Set(SimTunable.SubstepSeconds, substepNow, me);
			SimTunableResult cross = SimTunables.Set(SimTunable.ConductionMaxTemperature, 0.5f, me);
			SimTunableResult type = SimTunables.Set(SimTunable.GasPressureCap, 1, me);
			SimTunableResult nobody = SimTunables.Set(SimTunable.GasPressureCap, probe,
				default(ExtOwner));
			log("tunables refusals: nan " + nan + ", range " + range + ", ceiling " + raise
				+ ", substep " + world + ", same substep " + same + ", cross " + cross
				+ ", type " + type + ", owner " + nobody);
			check(nan == SimTunableResult.NotFinite && range == SimTunableResult.OutOfRange
					&& raise == SimTunableResult.OutOfRange,
				"the sim's own refusals come back through the void*: a NaN, a value under the "
				+ "row's min, and a CEILING raised above its stock value");
			check(world == SimTunableResult.WorldLoaded && same == SimTunableResult.Applied,
				"SubstepSeconds is refused while a world is loaded, and re-sending the value it "
				+ "already has is accepted (a config replayed after a load must not fail on it)");
			check(cross == SimTunableResult.CrossField,
				"a conduction ceiling under its floor is in range alone and refused as a pair");
			check(type == SimTunableResult.WrongType && nobody == SimTunableResult.InvalidOwner,
				"an int sent to a float row and a write with no owner are refused before "
				+ "anything is sent");
			check(SimTunables.GetFloat(SimTunable.GasPressureCap, float.NaN) == cap
					&& SimTunables.GetFloat(SimTunable.MaxTemperature, 0f) == maxT
					&& SimTunables.GetFloat(SimTunable.SubstepSeconds, 0f) == substepNow,
				"and no refused write changed anything");

			// ---- the managed mirrors follow the rows they mirror
			VerifyTunableMirrors(check, log, me);

			// ---- sim-tunables.json's parser
			string error;
			List<KeyValuePair<string, string>> parsed = SimTunablesConfig.Parse(
				" { \"GasPressureCap\": 0.25, \"RoomMixingEveryNTicks\" : 2,\n"
				+ "\"RandomScale\": \"0x38000100\", \"X\": -1.5e-3 } ", out error);
			check(parsed != null && parsed.Count == 4 && parsed[0].Value == "0.25"
					&& parsed[1].Value == "2" && parsed[2].Value == "0x38000100"
					&& parsed[3].Value == "-1.5e-3",
				"sim-tunables.json: a flat object of numbers and 0x strings parses, keeping each "
				+ "literal's text" + (error == null ? "" : " (" + error + ")"));
			check(SimTunablesConfig.Parse("{\"A\": 1, \"A\": 2}", out error) == null
					&& SimTunablesConfig.Parse("{\"A\": {\"B\": 1}}", out error) == null
					&& SimTunablesConfig.Parse("{\"A\": 1} x", out error) == null
					&& SimTunablesConfig.Parse("[1]", out error) == null
					&& SimTunablesConfig.Parse("{}", out error) != null,
				"and a repeated name, a nested object, trailing content and a non-object are "
				+ "refused whole, while an empty object is fine");
		}

		private static void VerifyTunableMirrors(Func<bool, string, bool> check,
			Action<string> log, ExtOwner me)
		{
			int gasCell = -1, deepLiquid = -1;
			for (int cell = 0; cell < Grid.CellCount && (gasCell < 0 || deepLiquid < 0); cell++)
			{
				if (!Grid.IsValidCell(cell) || Grid.Solid[cell])
				{
					continue;
				}
				int above = Grid.CellAbove(cell);
				if (gasCell < 0 && Grid.IsGas(cell) && !Grid.IsLiquid(cell))
				{
					gasCell = cell;
				}
				if (deepLiquid < 0 && Grid.IsLiquid(cell) && Grid.IsValidCell(above)
					&& Grid.IsLiquid(above))
				{
					deepLiquid = cell;
				}
			}

			// CellVolumeM3 -> PipeNetworkFacade's live cell volume, read through Solubility.
			if (gasCell >= 0)
			{
				float before = Solubility.FreeVolumeM3(gasCell);
				SimTunables.Set(SimTunable.CellVolumeM3, 2f, me);
				float tuned = Solubility.FreeVolumeM3(gasCell);
				SimTunables.Reset(SimTunable.CellVolumeM3, me);
				check(before == PipeNetworkFacade.CellVolumeM3 && tuned == 2f
						&& Solubility.FreeVolumeM3(gasCell) == before,
					"CellVolumeM3 moves the framework's cell volume: a gas cell's free volume "
					+ "reads " + before + " m^3, 2 with the row at 2, and " + before + " again");
			}
			else
			{
				log("tunables mirrors: no gas cell on this canvas; CellVolumeM3 NOT covered");
			}

			// FizzReferenceK -> Solubility's van 't Hoff reference. CO2's H(T) at the reference
			// temperature is the bare constant, so H(350 K) with the reference moved to 350 K must
			// be exactly H(298.15 K) with it at 298.15 K.
			float hAtRef = Solubility.HenryMolPerM3Pa(SimHashes.CarbonDioxide, SimHashes.Water,
				Solubility.ReferenceTemperatureK);
			float h350 = Solubility.HenryMolPerM3Pa(SimHashes.CarbonDioxide, SimHashes.Water, 350f);
			SimTunables.Set(SimTunable.FizzReferenceK, 350f, me);
			float h350Tuned = Solubility.HenryMolPerM3Pa(SimHashes.CarbonDioxide, SimHashes.Water,
				350f);
			SimTunables.Reset(SimTunable.FizzReferenceK, me);
			check(hAtRef > 0f && h350 != hAtRef && h350Tuned == hAtRef,
				"FizzReferenceK moves Solubility's reference: CO2 at 350 K reads the bare Henry "
				+ "constant once the reference is 350 K (" + h350Tuned.ToString("G6") + " = "
				+ hAtRef.ToString("G6") + ", untuned " + h350.ToString("G6") + ")");

			// GasConstantR -> Bubbles' ideal-gas density: doubling R exactly halves it.
			float rho = Bubbles.DensityKgPerM3(SimHashes.CarbonDioxide, 300f, 101325f);
			float r = SimTunables.GetFloat(SimTunable.GasConstantR, Bubbles.GasConstant);
			SimTunables.Set(SimTunable.GasConstantR, r * 2f, me);
			float rhoTuned = Bubbles.DensityKgPerM3(SimHashes.CarbonDioxide, 300f, 101325f);
			SimTunables.Reset(SimTunable.GasConstantR, me);
			check(rho > 0f && rhoTuned * 2f == rho
					&& Bubbles.DensityKgPerM3(SimHashes.CarbonDioxide, 300f, 101325f) == rho,
				"GasConstantR moves Bubbles' gas law: CO2's density halves exactly with R "
				+ "doubled (" + rho.ToString("G6") + " -> " + rhoTuned.ToString("G6") + ")");

			// FizzMinimumGasVolumeM3 -> Bubbles' gas-volume floor.
			if (gasCell >= 0)
			{
				float v = Bubbles.GasVolumeM3(gasCell);
				SimTunables.Set(SimTunable.FizzMinimumGasVolumeM3, 5f, me);
				float vTuned = Bubbles.GasVolumeM3(gasCell);
				SimTunables.Reset(SimTunable.FizzMinimumGasVolumeM3, me);
				check(v < 5f && vTuned == 5f && Bubbles.GasVolumeM3(gasCell) == v,
					"FizzMinimumGasVolumeM3 moves Bubbles' floor: a gas cell's volume ("
					+ v.ToString("G6") + " m^3) reads the floor, 5, once the floor is above it");
			}

			// StandardGravity -> Bubbles' hydrostatic term. Under two liquid cells the reading is
			// S + m g for a surface pressure S that gravity does not touch, so doubling g adds
			// exactly the old column term again.
			if (deepLiquid >= 0)
			{
				Vector3 at = Grid.CellToPos(deepLiquid) + new Vector3(0.5f, 0.1f, 0f);
				Vector2 pos = new Vector2(at.x, at.y);
				float p1 = Bubbles.PressureAtPa(pos);
				float g = SimTunables.GetFloat(SimTunable.StandardGravity, Bubbles.GravityMPerS2);
				SimTunables.Set(SimTunable.StandardGravity, g * 2f, me);
				float p2 = Bubbles.PressureAtPa(pos);
				SimTunables.Reset(SimTunable.StandardGravity, me);
				int cap = deepLiquid;
				while (Grid.IsValidCell(cap) && Grid.IsLiquid(cap))
				{
					cap = Grid.CellAbove(cap);
				}
				float surface = Grid.IsValidCell(cap) ? Bubbles.CapSurfacePressurePa(cap) : 0f;
				float predicted = p1 + (p1 - surface);
				check(p2 > p1 && Mathf.Abs(p2 - predicted) <= 1e-4f * p2
						&& Bubbles.PressureAtPa(pos) == p1,
					"StandardGravity moves Bubbles' hydrostatic pressure: doubling g adds the "
					+ "column term again (" + p1.ToString("G6") + " -> " + p2.ToString("G6")
					+ " Pa, predicted " + predicted.ToString("G6") + ")");
			}
			else
			{
				log("tunables mirrors: no liquid two cells deep on this canvas; StandardGravity "
					+ "NOT covered");
			}

			// Not covered live, and why: FizzSurfaceColumnCells changes the reading only over a
			// non-uniform gas column, and the two density fallbacks only for an element with no
			// density and no default mass, which no shipped element is. Each is one read of the
			// same cached row the arms above prove.
			log("tunables mirrors: FizzSurfaceColumnCells, FallbackConduitLiquidDensityKgM3 and "
				+ "FizzFallbackSolventDensityKgM3 NOT covered live (see the comment)");
		}

		private static void Log(string message)
		{
			Debug.Log("[OniFramework] SimExtSelfTest: " + message);
		}
	}
}
