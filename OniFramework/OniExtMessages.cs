// Message ids for the sim extensions the replacement SimDLL adds, which have no vanilla
// counterpart. Mirrors its abi/sim_abi_ext.h 1:1 -- if a value changes there, change it here
// too, nowhere else.
//
// The same ids are also published in C, as ONI_MSG_*, by its abi/sim_ext_api.h, which is the
// header a third party binds against. That file's ids are checked against abi/sim_abi_ext.h's
// at compile time by the native suite. THIS file is a third copy. If you add an id, add it in
// all three.
//
// WHAT CHECKS THIS FILE NOW, and what it does not. SimExtSelfTest (--ext-selftest, the
// EXTREGISTRY rig) walks the message table the DLL publishes -- SimExtMessages, over
// SIM_ExtMessageCount / SIM_ExtMessageDescribe -- and compares it against these constants in
// BOTH directions: every constant here must be implemented by the DLL under the same id and the
// k-prefixed spelling of the same name, and every id the DLL publishes must have a constant
// here. The managed side of that comparison is enumerated BY REFLECTION over this class, on
// purpose: a hand-typed list in the check would be a fourth copy and would prove nothing.
//
// So the check is real, but it is a LIVE gate, not the compiler. An id added here and nowhere
// else still builds; it fails the next EXTREGISTRY run. Adding it in all three remains the rule.
namespace OniFramework
{
	internal static class OniExtMessages
	{
		// oni_sim::ext::kSetCellThermalMassBonus, abi/sim_abi_ext.h.
		public const int SetCellThermalMassBonus = 0x4F4E4931; // "ONI1"

		// oni_sim::ext::kInjectGasSpecies, abi/sim_abi_ext.h. Seeds the volume-fractions gas
		// mixture (see GasMixtureFacade) -- the first one any world receives activates mixing
		// for that world.
		public const int InjectGasSpecies = 0x4F4E4932; // "ONI2"

		// oni_sim::ext::kRemoveVanillaMass, abi/sim_abi_ext.h. Symmetric counterpart to
		// InjectGasSpecies -- takes mass out of a cell's vanilla PhaseEntry so a caller can
		// pair the two into a conserving transfer instead of duplicating matter.
		public const int RemoveVanillaMass = 0x4F4E4933; // "ONI3"

		// oni_sim::ext::kConvertToVanillaMass, abi/sim_abi_ext.h. The reverse transfer --
		// mixture layer back into vanilla -- done as one atomic native operation instead of a
		// remove-then-add pair, so it can't create mass regardless of what the caller asks for.
		public const int ConvertToVanillaMass = 0x4F4E4934; // "ONI4"

		// oni_sim::ext::kPromoteRoom, abi/sim_abi_ext.h. Flips whichever room a cell belongs
		// to from vanilla-owned to mixture-owned. Promote-only -- there is no demote id.
		public const int PromoteRoom = 0x4F4E4935; // "ONI5"

		// oni_sim::ext::kSetInvertedGravityElement, abi/sim_abi_ext.h. Flips the vertical sign
		// of one element's own turn in StepFlow's liquid branch (physics.h) -- that element
		// falls up and pools under solid ceilings instead of on floors. -1 clears the effect.
		// A real physics-kernel change, not a managed-side per-frame swap.
		public const int SetInvertedGravityElement = 0x4F4E4936; // "ONI6"

		// oni_sim::ext::kSetMolecularMass, abi/sim_abi_ext.h. Overrides the molecular mass the
		// sim divides by when it counts moles, keyed on a SimHashes id (Element::id) rather than
		// an element-table index. Klei's table stores ATOMIC mass for the diatomic gases, which
		// made every reported diatomic pressure exactly 2x high; the native side seeds its own
		// corrections, and this message is what lets MaterialPropertyRegistry -- and therefore a
		// third-party mod registering a new gas -- be the authority instead.
		public const int SetMolecularMass = 0x4F4E4937; // "ONI7"

		// oni_sim::ext::kSetBuildingWasteHeatKilowatts, abi/sim_abi_ext.h. The power -> heat
		// rule's rate for one building (see PowerHeat), summed with Klei's own
		// SelfHeatKilowattsWhenActive at the same post-clamp line in the native
		// StepBuildingHeatExchange. Payload is { int32 simHandle, float kilowatts }.
		//
		// A field the sim owns, rather than a Harmony patch that makes vanilla's own
		// OperatingKilowatts getter report a larger number: the sim can then tell the two
		// apart, so the native energy ledger can report how much of a colony's heat came
		// from the power grid instead of only that heat appeared.
		public const int SetBuildingWasteHeatKilowatts = 0x4F4E4938; // "ONI8"

		// oni_sim::ext::kSetBuildingExhaust, abi/sim_abi_ext.h. Klei's
		// ExhaustKilowattsWhenActive, as a rate the sim owns and integrates over its own
		// substeps. Payload is { int32 simHandle, float kilowatts, float maxTemperature }.
		//
		// The sim performs the whole of StructureTemperatureComponents.ExhaustHeat itself --
		// the same per-cell split, the same 1.5 kg mass factor, the same ceiling -- and puts
		// the part that cannot be delivered into the building's own body instead of destroying
		// it. See ExhaustHeat for the managed half, which must SUPPRESS vanilla's own delivery
		// when it sends this or the heat lands twice.
		public const int SetBuildingExhaust = 0x4F4E4939; // "ONI9"

		// oni_sim::ext::kSetBuildingRadiation, abi/sim_abi_ext.h. Emissivity and radiating area
		// for one building. Payload is { int32 simHandle, float radiationFactor,
		// float surfaceAreaM2 }; an area <= 0 means the building's own footprint.
		//
		// The same structure as Stationeers' radiative loss (AtmosphereHelper). The outlet
		// that stops a bounced exhaust from simply cooking every machine in a vacuum.
		public const int SetBuildingRadiation = 0x4F4E493A; // "ONI:"

		// oni_sim::ext::kSetEnvironmentTemperature, abi/sim_abi_ext.h. The temperature of the
		// reservoir radiating bodies shed into -- the sim's stand-in for Stationeers'
		// PlanetaryAtmosphereSimulation.
		// Payload is { float kelvin }.
		public const int SetEnvironmentTemperature = 0x4F4E493B; // "ONI;"

		// oni_sim::ext::kSetRandomState, abi/sim_abi_ext.h. Writes the sim's LCG state
		// verbatim, which is the one input to a run that is otherwise not reproducible.
		// Payload is { uint32 state }.
		//
		// AllocateCells -- the message a main-game LOAD boots through -- seeds the stream
		// from the wall clock, which is Klei's own behaviour and deliberately preserved: a
		// save loaded twice shuffles its gas differently. This does not change that default.
		// It exists so a caller that wants a reproducible run can ask for one, and a world
		// that never sends it behaves exactly as before.
		//
		// Pairs with the SIM_DebugRandomState export, which is the getter. Read the state,
		// keep it beside a save blob, send it back to return to that point in the stream --
		// a blob alone does not carry it, because rng_ is not in the sim's save format.
		//
		// Queued like every other id here, so it lands at the top of the NEXT frame, ahead
		// of that frame's physics. Send it before the first frame that must be reproducible.
		//
		// Not a seed, a state: the sim does no scrambling, matching SimData's constructor,
		// so a value round-tripped from SIM_DebugRandomState reproduces the stream exactly.
		// Every uint32 is legal, 0 included.
		public const int SetRandomState = 0x4F4E493C; // "ONI<"

		// oni_sim::ext::kSetSchedulingState, abi/sim_abi_ext.h. The rest of what a save blob
		// does not carry: the SimData scheduling counters. Payload is
		// { int32 skip_physics_frames, int32 pressure_dir, int32 shuffle_dir,
		//   float substep_carry, uint16 displace_rotation, uint8 first_physics_substep,
		//   uint8 vf_active } -- 20 bytes, 4-byte packed. See SimScheduling.State.
		//
		// Both AllocateCells and Load reset these to their fresh-allocation values, so a
		// restored world runs its physics in a different order than the run it was captured
		// from. Measured: a replay reproduced its original run for exactly one frame and then
		// diverged, 26 of 35 replayed ticks wrong; with this message, all 40 seeks of the
		// same sweep reproduce bit for bit.
		//
		// Pairs with the SIM_DebugSchedulingState export, which is the getter.
		//
		// Queued like every other id here: it lands at the top of the FRAME AFTER the one it
		// was sent during, ahead of that frame's physics. (An earlier version of this comment
		// said two frames. Measured since, by reading the counters back after each frame of a
		// restore: at +0 frames they are still the Load's own defaults, at +1 they are the
		// restored values with one substep already run on top.) Send the captured
		// skip_physics_frames as it was; re-forcing 1 here drops a physics frame from every
		// replay, because the frame this lands on is the one the Load already marked skipped.
		public const int SetSchedulingState = 0x4F4E493D; // "ONI="

		// oni_sim::ext::kSetStableTicks, abi/sim_abi_ext.h. The last piece a save blob does not
		// carry: World::stable_ticks_, one byte per PADDED cell, holding how
		// many substeps an unstable solid has left before it falls. Payload is
		// { int32 count } followed by exactly `count` bytes -- the only variable-length message
		// in this set. See SimStableTicks.
		//
		// Every allocation resets the array to the all-0x1f reroll sentinel, and every load
		// goes through one, so a restored world has forgotten every countdown and rerolls each
		// one it reaches. That reroll is the only draw the unstable path makes on the random
		// stream, so the symptom is sand falling on the wrong tick AND a stream that ends up
		// out of step despite being restored. Measured one replayed frame past a checkpoint:
		// 2,234 of 256,944 cells differ, 20.392861 kg in two cells of about 10 kg.
		//
		// A complete checkpoint is FOUR things -- the save blob, SetRandomState,
		// SetSchedulingState, and this. Any three of them leave a replay that can diverge, and
		// which seeds show it is a property of the world rather than of the checkpoint spacing.
		//
		// Pairs with the SIM_DebugStableTicks export, which is the getter and also reports the
		// array's length, so a caller never derives the padded cell count itself.
		//
		// Queued, and sized against the world that is loaded when it drains: send it after the
		// Load it belongs to. A length that is not that world's padded cell count is rejected
		// and logged rather than partially applied.
		public const int SetStableTicks = 0x4F4E493E; // "ONI>"

		// oni_sim::ext::kSetDiseaseGrowth, abi/sim_abi_ext.h. The fifth thing a save blob does
		// not carry (it was believed to be the last; see SetRegistryState below): World::disease_accum_ (a float per PADDED cell, the growth
		// remainder banked toward the next whole disease unit) and World::disease_infest_ (a
		// byte per padded cell, how long the cell has been infested). Payload is
		// { int32 count }, then `count` floats, then `count` bytes -- the second
		// variable-length message in this set and the only one carrying two arrays. See
		// SimDiseaseGrowth.
		//
		// Klei's SaveDisease is eight bytes, a hash and a count, so neither array is in the
		// save format and every load zeroes both. Faithful and deliberately unchanged; this
		// message lets a caller reconstructing a run opt out of it.
		//
		// Measured: a scrub timeline that reported every replayed tick reproducing was hashing
		// three of the twelve arrays GameDataUpdate publishes. Widened to all twelve, the same
		// 40-tick sweep failed 26 of 35 replayed ticks in the disease arrays alone -- random
		// stream in step, total mass bit-identical, at every seed tried. Exact stops and short
		// replays passed throughout, because a remainder restarted at zero takes several ticks
		// to fall a whole unit behind.
		//
		// Pairs with the SIM_DebugDiseaseGrowth export, which is the getter, returns both
		// arrays in one call, and also reports the length.
		//
		// Queued, and sized against the world that is loaded when it drains: send it after the
		// Load it belongs to. A length that is not that world's padded cell count is rejected
		// and logged rather than partially applied.
		public const int SetDiseaseGrowth = 0x4F4E493F; // "ONI?"

		// oni_sim::ext::kSetRegistryState, abi/sim_abi_ext.h. The SIXTH thing, and the first
		// that is not per-cell state at all: the four component registries -- buildings,
		// element chunks, the element/disease flow components, the radiation emitters. The
		// payload IS the blob SIM_DebugRegistryState produced, with no header in front of it,
		// because the blob carries its own magic, version and lengths. See SimRegistryState.
		//
		// AllocateCells clears all four registries and no save blob carries any of them. In
		// ordinary play that is invisible, because the game re-registers every handle it holds
		// after a load. A caller that is not the game cannot, so for it a restored world has
		// no colony in it.
		//
		// Re-sending the original registration messages does NOT substitute for this, and the
		// measurement is what settles it: a registration is a handle on a body that then
		// evolves every substep, so replaying the recorded registrations restores the topology
		// at its first-registered values and a checkpoint comes back stale by its own age.
		// Shortening the checkpoint interval made it worse -- 3 of 17 replayed ticks
		// reproducing at interval 2 against 9 at interval 10 -- which is the signature of
		// stale state rather than of a long replay. Exact stops passed throughout, because a
		// stop restores and publishes without stepping physics.
		//
		// Pairs with the SIM_DebugRegistryState export, which is the getter and reports the
		// size. The size is not derivable from the world's dimensions -- it depends on how
		// much has been registered -- so the sizing call is the only way to learn it.
		//
		// Queued: send it after the Load it belongs to, and do NOT also replay the original
		// registration messages, which the sim drains after this one and which would then
		// double every registration this restored. Applied whole or not at all.
		public const int SetRegistryState = 0x4F4E4940; // "ONI@"

		// ------------------------------------------------------------------------------
		// THE THREE EXTENSION REGISTRIES. Everything above this line
		// is a message with one fixed meaning; everything below it is a message that carries a
		// NAME, so a mod can add simulation-side data the SimDLL was never written to hold.
		//
		// The managed surface is SimExtCellProperties, SimExtElementAttributes,
		// SimExtEventStreams and SimExtFrame. These ids are here for the same reason the rest
		// are -- one place that mirrors abi/sim_abi_ext.h -- and are not meant to be used
		// directly.
		// ------------------------------------------------------------------------------

		// oni_sim::ext::kRegisterCellProperty, abi/sim_abi_ext.h. Registry 1: declares a
		// per-cell array by name, type, persistence class and arity. IMMEDIATE, and one of the
		// few messages in this file with a RETURN VALUE -- the property index, or a negated
		// ext::ExtRegisterResult, read through the IntPtr SIM_HandleMessage hands back.
		//
		// Registration opens at SIM_Initialize (which destroys the previous sim, so it reopens on
		// every load) and CLOSES at the world allocation a few lines later, because every
		// per-cell array in a world is the same length for the life of that world. See
		// SimExtRegistrar for the window.
		public const int RegisterCellProperty = 0x4F4E4941; // "ONIA"

		// oni_sim::ext::kSetCellProperty, abi/sim_abi_ext.h. Writes one component of one cell
		// of a registered property. Payload is { int32 cellIdx, int32 propertyIdx,
		// int32 component, uint32 valueBits }; the bits are reinterpreted per the property's
		// registered type. Queued, so it lands on the frame after the one it was sent during --
		// the same latency SetInsulationValue has.
		public const int SetCellProperty = 0x4F4E4942; // "ONIB"

		// oni_sim::ext::kSetExtCellState, abi/sim_abi_ext.h. The checkpoint counterpart of the
		// registry: it restores every registered per-cell array at once, from the opaque blob
		// SIM_DebugExtCellStateAll hands back. Payload IS the blob, first byte to last.
		//
		// Nothing managed sends it; the caller is an offline replay harness restoring a
		// checkpoint.
		//
		// Queued, and sized against the world that is loaded when it drains: send it after the
		// Load it belongs to, and do not also replay the original SetCellProperty writes, which
		// would then apply on top of what this restored.
		public const int SetExtCellState = 0x4F4E4943; // "ONIC"

		// oni_sim::ext::kPublishCellProperty, abi/sim_abi_ext.h. Subscribes (or unsubscribes) a
		// registered property to the per-frame published descriptor table, which is the cheap
		// route for reading a whole property every tick. Payload is { int32 propertyIdx,
		// int32 enable }.
		//
		// Opt-in per property because publishing COPIES the bytes into the published frame --
		// that copy is what buys the pointer its tick-long lifetime while kernels are still
		// writing the live storage. See SimExtFrame.
		public const int PublishCellProperty = 0x4F4E4944; // "ONID"

		// oni_sim::ext::kSubscribeEventStream, abi/sim_abi_ext.h. Registry 3: starts or stops
		// COLLECTING a natively-declared event stream. Payload is { int32 streamIdx,
		// int32 enable }.
		//
		// There is deliberately no register message for a stream: a stream reports something
		// the simulation observed, so it is declared by the kernel that produces it. The
		// managed surface is discover, subscribe, read.
		public const int SubscribeEventStream = 0x4F4E4945; // "ONIE"

		// oni_sim::ext::kRegisterElementAttribute, abi/sim_abi_ext.h. Registry 2: declares a
		// per-element attribute by name, type and arity. IMMEDIATE, and returns the attribute
		// index the same way RegisterCellProperty does.
		//
		// Unlike registry 1 this never closes -- nothing here is sized to the world -- and
		// nothing it holds is ever saved, because an attribute of an element is content data
		// that the owning mod re-pushes every session.
		public const int RegisterElementAttribute = 0x4F4E4946; // "ONIF"

		// oni_sim::ext::kSetElementAttribute, abi/sim_abi_ext.h. Writes one component of one
		// element's attribute, or clears that element's whole entry. Payload is
		// { int32 attrIdx, int32 idHash, int32 component, uint32 valueBits, int32 clear };
		// idHash is a SimHashes value, never an element-table index. IMMEDIATE.
		//
		// `clear` is an explicit field rather than a magic value because "unset" and "zero" are
		// different answers for a sparse per-element store -- SetMolecularMass above spells
		// removal as a non-positive mass, which only works because no real molar mass is
		// negative.
		public const int SetElementAttribute = 0x4F4E4947; // "ONIG"

		// oni_sim::ext::kSetBuildingConvection, abi/sim_abi_ext.h. Stationeers-shaped
		// convection between one building's body and its footprint cells. Payload is
		// { int32 simHandle, float convectionFactor, float surfaceAreaM2 }; an area <= 0 means
		// the building's own footprint, and a factor <= 0 switches the term off.
		//
		// The same form as Stationeers' convection (AtmosphereHelper): 100 W/(m^2 K) times the
		// area, the factor, and each cell's HeatExchangeRatio(). Sending it also makes that
		// building's own conduit, if it is one, stop applying the cell ratio of
		// ConduitNetworkPolicy.Convection -- one ratio, on one leg. See Convection for the
		// managed half.
		public const int SetBuildingConvection = 0x4F4E4948; // "ONIH"

		// oni_sim::ext::kSetWorldEnvironment, abi/sim_abi_ext.h. The planet a world is standing
		// on: a radiative sink, a solar irradiance, and a surface atmosphere, per world. Payload
		// is { int32 worldIndex, float sinkKelvin, float peakIrradianceWPerM2, float fullSunLux,
		// float solarAbsorptivity, float surfacePressureKPa, float boundaryTemperatureK,
		// float boundaryRate, int32 boundaryElement } -- 36 bytes.
		//
		// worldIndex is the position in DefineWorldOffsets' list (ClusterManager's
		// WorldContainers order), and a negative one sets the record every world without its own
		// falls back to. Every field is inert at zero, so a world that is never sent one behaves
		// exactly as vanilla ONI does. See PlanetaryEnvironment for the managed half.
		public const int SetWorldEnvironment = 0x4F4E4949; // "ONII"

		// oni_sim::ext::kSetWorldSun, abi/sim_abi_ext.h. The direction a world's sun shines from:
		// { int32 worldIndex, float dirX, float dirY, int32 enabled } -- 16 bytes. (dirX, dirY) is
		// the vector toward the sun in the grid plane, east (x max) and up positive, normalised by
		// the sim; dirY <= 0 lights nothing, enabled 0 forgets the record. A world with a sun gets
		// a direct-beam field its solar terms read instead of the vertical sky view.
		// See SunPath for the managed half.
		public const int SetWorldSun = 0x4F4E494A; // "ONIJ"

		// oni_sim::ext::kSetCellRadiation, abi/sim_abi_ext.h. The EIGHTH thing a checkpoint needs:
		// the whole radiation field, { int32 count } then `count` floats, one per PADDED cell. The
		// blob carries radiation, but Load clears it in Vacuum and Void cells as Klei's does, and
		// a running world has some there. IMMEDIATE -- the first immediate checkpoint component --
		// because radiation is published and the first frame after a Load publishes before a
		// queued message drains. See SimCellRadiation.
		public const int SetCellRadiation = 0x4F4E494B; // "ONIK"

		// oni_sim::ext::kSetBlockedGasAddPolicy, abi/sim_abi_ext.h. { int32 policy }: 0 vanilla
		// (a blocked ModifyCell gas add deletes the smaller mass), 1 promote the room, move its gas
		// into the mixture and mix. Queued; not saved, so it is sent once per session.
		// See GasMixtureFacade.SetBlockedGasAddPolicy.
		public const int SetBlockedGasAddPolicy = 0x4F4E494C; // "ONIL"

		// oni_sim::ext::kSetCellPropertyTransport, abi/sim_abi_ext.h. { int32 propertyIdx, int32
		// transport }: 0 static (every property's default), 1 the property is an amount carried
		// by the liquid in its cell and moves, splits and leaves with it. F32 only; anything else
		// is refused on sim.message_refused. Queued; not saved, so it is sent once per session.
		// See SimExtCellProperties.SetTransport and LiquidPayload.
		public const int SetCellPropertyTransport = 0x4F4E494D; // "ONIM"

		// oni_sim::ext::kAddCellPropertyAmount, abi/sim_abi_ext.h. { int32 cellIdx, int32
		// propertyIdx, int32 component, float amount }: read, add, write on the sim's side of
		// the queue, so two adds in flight both land. F32 only; a non-finite amount or a sum
		// below zero is refused on sim.message_refused rather than clamped. Queued.
		// See SimExtCellProperties.AddFloat.
		public const int AddCellPropertyAmount = 0x4F4E494E; // "ONIN"

		// oni_sim::ext::kRegisterField, abi/sim_abi_ext.h. { int32 propertyIdx, int32
		// attributeIdx, float attributeFallback, int32 law, int32 combine, int32 decayMode,
		// float decayKeep, float floorValue, float clampLo, float clampHi }: attaches a
		// propagation rule to a cell property that is ALREADY registered -- a field is not new
		// storage, it is a solver bolted onto an F32 arity-1 property somebody else declared.
		// IMMEDIATE, and it returns the field index, or a negated ExtRegisterResult.
		// See SimFields.
		public const int RegisterField = 0x4F4E494F; // "ONIO"

		// oni_sim::ext::kSetFieldSource, abi/sim_abi_ext.h. { int32 fieldIdx, int32 sourceId,
		// int32 kind, float strength, int32 target, int32 radiusX, int32 radiusY, float
		// coneDirection, float coneAngle, float dirX, float dirY }: adds, replaces or removes one
		// source of one field, keyed by the OWNER's id so that re-pushing after a load is
		// idempotent. strength == 0 removes. Queued; read by StepFields. Not saved -- a field's
		// VALUES persist, its rule and its sources are content the owner re-pushes.
		// See SimFields.
		public const int SetFieldSource = 0x4F4E4950; // "ONIP"

		// oni_sim::ext::kSetPayloadMixing, abi/sim_abi_ext.h. { int32 propertyIdx, float share }:
		// how fast a liquid-carried amount evens out across the water holding it -- the fraction
		// of a neighbour pair's CONCENTRATION gap StepPayloadMixing closes per pair per substep.
		// Transport (ONIM above) moves an amount with its water; this is what spreads it through
		// water that is not moving, and without it a pond carbonated in one column read exactly
		// zero one tile away. 0 switches mixing off for the property, a value outside 0..1 is
		// clamped, a propertyIdx nothing registered is refused on sim.message_refused, and -1
		// means every liquid-following property at once. sim.dissolved_mass starts at 0.125.
		// Queued; not saved, so it is sent once per session.
		// See SimExtCellProperties.SetMixing and DissolvedGas.MixingShare.
		public const int SetPayloadMixing = 0x4F4E4951; // "ONIQ"

		// oni_sim::ext::kSetDissolvedTint, abi/sim_abi_ext.h. { int32 enabled, float fullScale,
		// float maxBlend, float weight[8], uint32 colour[8] }: the liquid property texture blends a
		// cell's rendered colour towards the colour of whatever is dissolved in it, so that a
		// carbonated pond does not render byte-identically to a plain one. The colours and the
		// per-lane mole weights come from the caller because a lane's gas is a managed-side
		// assignment and the sim has no element colours at all. Off until sent, and not saved.
		// "ONIR" is reserved. See LiquidTint.
		public const int SetDissolvedTint = 0x4F4E4953; // "ONIS"

		// oni_sim::ext::kSetEffervescence, abi/sim_abi_ext.h. 248 bytes: { int32 enabled, float
		// margin, float ratePerSecond, float periodSeconds, float minReleaseKg, float henry[8],
		// float vantHoff[8], float molarKg[8], float partialMolarVolume[8], int32 solventCount,
		// int32 solventElementIdx[8], float solventFactor[8], float solventDensity[8] }: a liquid
		// cell holding more dissolved gas than the pressure on it can keep in solution releases the
		// excess as bubbles, on sim.liquid_payload_released with reason 9. Off until sent, and not
		// saved. "ONIT" is reserved. See Effervescence.
		public const int SetEffervescence = 0x4F4E4955; // "ONIU"

		// oni_sim::ext::kSetTunable, abi/sim_abi_ext.h. 16 bytes: { int32 tunableId, uint32
		// reserved, uint64 valueBits }: sets one row of the sim's tunable table (a float or an
		// int32 in the low 32 bits, a double in all 64), or, with tunableId -1, restores every row
		// to its default. IMMEDIATE, and it RETURNS its answer: 0, or the refusal reason (4-8 are
		// its own). Immediate because SubstepSeconds may change only while no world exists, and a
		// queued message drains only inside a frame, which needs a world. Not saved. See
		// SimTunables.
		public const int SetTunable = 0x4F4E4956; // "ONIV"

		// oni_sim::ext::kSetVisibilityState, abi/sim_abi_ext.h. The NINTH thing a checkpoint
		// needs: the game's visibility mask, three buffers deep. { int32 count, int32 simSlot }
		// then three buffers of `count` bytes, one per game cell. Every allocate and load empties
		// them, so a replay's first frames refused the falling liquid its run handed over.
		// IMMEDIATE, like SetCellRadiation, because the next frame's call writes one of these
		// buffers before a queued message would drain. See SimVisibilityState.
		public const int SetVisibilityState = 0x4F4E4957; // "ONIW"

		// oni_sim::ext::kSetLoadIsRestore, abi/sim_abi_ext.h. { int32 restore, int32 reserved }:
		// the next Load is a checkpoint restore, not a save, so it skips the load-time state
		// transition that moves a cell outside its element's range one step. A run publishes such
		// cells, and a restore that transitioned them replayed from a state the run never held.
		// One-shot, IMMEDIATE, and sent BEFORE the Load. The game never sends it; the framework
		// has it here only so the live table comparison knows the id.
		public const int SetLoadIsRestore = 0x4F4E4958; // "ONIX"
	}
}
