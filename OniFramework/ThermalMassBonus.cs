using System;

namespace OniFramework
{
	/// <summary>
	/// Gives an individual cell extra heat capacity (J/K) on top of whatever its element's
	/// own mass*specificHeatCapacity already provides -- something no vanilla message can
	/// do, since vanilla heat capacity comes entirely from the per-element table.
	///
	/// Backed by the replacement SimDLL's <c>SetCellThermalMassBonus</c> extension
	/// (sim/simdll.cpp, physics.h's conduction kernel; abi/sim_abi_ext.h for the message). The
	/// sim extension exists, and this is what makes it reachable from a Harmony mod
	/// without that mod poking the SimDLL directly.
	///
	/// Only present when the custom SimDLL is installed. On a vanilla SimDLL this call is a
	/// silent no-op (the id is unrecognised, the message is dropped) rather than a crash --
	/// deliberately, so a flagship mod built against this API degrades instead of faulting
	/// if someone runs it over the stock DLL.
	/// </summary>
	public static class ThermalMassBonus
	{
		/// <param name="cell">A game cell index, e.g. from <c>Grid.PosToCell</c> -- the same
		/// space every other per-cell sim message (SetInsulationValue, ModifyCell, ...)
		/// already uses.</param>
		/// <param name="bonusJoulesPerKelvin">Extra heat capacity for this cell, replacing
		/// (not accumulating with) any previous bonus on it. 0 removes the bonus. Negative
		/// values are accepted by the sim but not clamped -- see abi/sim_abi_ext.h.</param>
		public static unsafe void Set(int cell, float bonusJoulesPerKelvin)
		{
			byte[] payload = new byte[8];
			Buffer.BlockCopy(BitConverter.GetBytes(cell), 0, payload, 0, 4);
			Buffer.BlockCopy(BitConverter.GetBytes(bonusJoulesPerKelvin), 0, payload, 4, 4);
			fixed (byte* msg = payload)
			{
				global::Sim.SIM_HandleMessage(OniExtMessages.SetCellThermalMassBonus, payload.Length, msg);
			}
		}

		/// <summary>Convenience for the common case: remove any bonus on this cell.</summary>
		public static void Clear(int cell) => Set(cell, 0f);
	}
}
