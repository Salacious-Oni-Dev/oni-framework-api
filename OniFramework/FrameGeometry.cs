using System.Globalization;
using UnityEngine;

namespace OniFramework
{
	/// <summary>
	/// The mapping between world cells and pixels in a captured frame, recorded alongside every
	/// frame so a capture can be read back by CELL COORDINATE rather than by eye.
	///
	/// WHY THIS EXISTS. Without it, a fault in a rig is found by a human looking at a screenshot
	/// and saying where it is, and each one costs a round trip. A frame with no geometry attached is just pixels: nothing in it says which cell
	/// is where, so neither the rig nor anyone reading the recording afterwards can go from "the
	/// reservoir at (24,20) looks wrong" to the pixels showing it. With the geometry recorded, both
	/// directions work -- a detector can point at a cell and have the frame region be findable, and
	/// a fault reported later can be located in frames that were already captured, without
	/// re-running anything.
	///
	/// THE TRANSFORM IS MEASURED, NOT DERIVED. It would be easy to compute pixels-per-cell from the
	/// camera's orthographic size and the screen height, and that formula is probably right -- but
	/// "probably right" multiplied across every crop is how a diagnostic tool ends up quietly
	/// pointing at the wrong tile. Instead <see cref="Capture"/> asks the live camera where two
	/// known world points actually land (<c>Camera.WorldToScreenPoint</c>) and stores the affine
	/// transform that fits them. Whatever the camera is doing -- letterboxing, a viewport rect, a
	/// projection quirk -- the recorded numbers describe what actually happened to that frame.
	///
	/// COORDINATE CONVENTIONS, because there are three and they disagree:
	///   - CELL space: integer (x, y), y increasing upward. Cell (x,y) covers world x..x+1, y..y+1
	///     -- confirmed from <c>Grid.CellToPos</c>, which is a plain multiply by
	///     <c>CellSizeInMeters</c> (1) with no offset, so a cell's ORIGIN is its bottom-left corner
	///     and its centre is +0.5.
	///   - SCREEN space: Unity pixels, origin BOTTOM-left, y increasing upward.
	///   - IMAGE space: what is actually in the JPEG, origin TOP-left, y increasing DOWNWARD.
	/// Everything written to the sidecar is in IMAGE space, because that is the space anything
	/// reading the file back will be cropping in.
	/// </summary>
	public struct FrameGeometry
	{
		/// <summary>Frame width in pixels.</summary>
		public int ScreenWidth;

		/// <summary>Frame height in pixels.</summary>
		public int ScreenHeight;

		/// <summary>Pixels per cell along X. Separate from Y only so a non-square pixel would be
		/// visible in the data rather than silently averaged away.</summary>
		public float PixelsPerCellX;

		/// <summary>Pixels per cell along Y.</summary>
		public float PixelsPerCellY;

		/// <summary>Image-space X of cell column 0's left edge. May be off-frame (negative, or
		/// past the width); that is normal and the crop maths handles it.</summary>
		public float OriginPixelX;

		/// <summary>Image-space Y of cell row 0's BOTTOM edge. Because image Y grows downward,
		/// this is the LARGEST y of the row-0 cell, not the smallest.</summary>
		public float OriginPixelY;

		/// <summary>Camera position, recorded for diagnostics; the transform above does not depend
		/// on it.</summary>
		public float CameraX;

		/// <summary>See <see cref="CameraX"/>.</summary>
		public float CameraY;

		/// <summary>Camera orthographic size, recorded for diagnostics.</summary>
		public float OrthographicSize;

		/// <summary>True when <see cref="Capture"/> found a usable camera and grid.</summary>
		public bool Valid;

		/// <summary>
		/// Measures the current frame's cell-to-pixel transform off the live camera.
		///
		/// Two probe points a good distance apart, so the division that derives the scale is not
		/// dominated by the sub-pixel error in either. Ten cells is far enough to be accurate and
		/// close enough that both stay on screen at any sane zoom -- and staying on screen is not
		/// actually required, since <c>WorldToScreenPoint</c> extrapolates happily past the
		/// viewport, but it keeps the numbers readable when someone inspects the sidecar by hand.
		/// </summary>
		public static FrameGeometry Capture()
		{
			var geometry = default(FrameGeometry);
			geometry.ScreenWidth = Screen.width;
			geometry.ScreenHeight = Screen.height;

			Camera camera = CameraController.Instance != null && CameraController.Instance.baseCamera != null
				? CameraController.Instance.baseCamera
				: Camera.main;
			if (camera == null || Grid.CellCount <= 0)
			{
				geometry.Valid = false;
				return geometry;
			}

			const float ProbeSpan = 10f;
			Vector3 nearPoint = camera.WorldToScreenPoint(new Vector3(0f, 0f, 0f));
			Vector3 farPoint = camera.WorldToScreenPoint(new Vector3(ProbeSpan, ProbeSpan, 0f));

			float perCellX = (farPoint.x - nearPoint.x) / ProbeSpan;
			float perCellY = (farPoint.y - nearPoint.y) / ProbeSpan;
			if (Mathf.Abs(perCellX) < 1e-4f || Mathf.Abs(perCellY) < 1e-4f)
			{
				geometry.Valid = false;
				return geometry;
			}

			geometry.PixelsPerCellX = perCellX;
			geometry.PixelsPerCellY = perCellY;
			geometry.OriginPixelX = nearPoint.x;

			// Screen space is bottom-origin and image space is top-origin, so the flip happens
			// exactly once, here, and everything downstream is image space.
			geometry.OriginPixelY = geometry.ScreenHeight - nearPoint.y;

			Vector3 cameraPosition = camera.transform.position;
			geometry.CameraX = cameraPosition.x;
			geometry.CameraY = cameraPosition.y;
			geometry.OrthographicSize = camera.orthographicSize;
			geometry.Valid = true;
			return geometry;
		}

		/// <summary>
		/// The image-space pixel rectangle covering cells
		/// (<paramref name="minX"/>,<paramref name="minY"/>) to
		/// (<paramref name="maxX"/>,<paramref name="maxY"/>) inclusive, expanded by
		/// <paramref name="padCells"/> cells on every side.
		///
		/// Returned unclamped, so a caller can tell the difference between "this region is at the
		/// edge of frame" and "this region is off frame entirely" rather than both collapsing to a
		/// rectangle flush with the border.
		/// </summary>
		public Rect CellRectToImageRect(int minX, int minY, int maxX, int maxY, int padCells = 0)
		{
			float left = OriginPixelX + (minX - padCells) * PixelsPerCellX;
			float right = OriginPixelX + (maxX + 1 + padCells) * PixelsPerCellX;

			// Image Y grows downward while cell Y grows upward, so the cell with the LARGER y is
			// the one with the SMALLER image y. Getting this backwards produces a crop that is
			// plausibly sized and in the wrong place, which is the worst kind of wrong.
			float top = OriginPixelY - (maxY + 1 + padCells) * PixelsPerCellY;
			float bottom = OriginPixelY - (minY - padCells) * PixelsPerCellY;

			return Rect.MinMaxRect(Mathf.Min(left, right), Mathf.Min(top, bottom),
				Mathf.Max(left, right), Mathf.Max(top, bottom));
		}

		/// <summary>The image-space rectangle covering one cell, padded by whole cells.</summary>
		public Rect CellToImageRect(int cell, int padCells = 0)
		{
			Grid.CellToXY(cell, out int x, out int y);
			return CellRectToImageRect(x, y, x, y, padCells);
		}

		/// <summary>
		/// The cell under an image-space pixel, or false when that pixel is outside the grid.
		/// The inverse of <see cref="CellRectToImageRect"/>, for reading a coordinate off a frame
		/// someone has pointed at.
		/// </summary>
		public bool ImagePixelToCell(float imageX, float imageY, out int cell)
		{
			cell = -1;
			if (!Valid)
			{
				return false;
			}

			int x = Mathf.FloorToInt((imageX - OriginPixelX) / PixelsPerCellX);
			int y = Mathf.FloorToInt((OriginPixelY - imageY) / PixelsPerCellY);
			if (x < 0 || y < 0 || x >= Grid.WidthInCells || y >= Grid.HeightInCells)
			{
				return false;
			}

			cell = Grid.XYToCell(x, y);
			return true;
		}

		/// <summary>
		/// One JSON object, for a JSON Lines sidecar. Hand-built rather than run through a
		/// serializer because this is called once per captured frame and the shape is four numbers
		/// and a name; invariant culture throughout, so a machine with comma decimal separators
		/// does not write a file nothing can parse.
		/// </summary>
		public string ToJson(int frameIndex, string fileName, float realtimeSeconds, float simSeconds)
		{
			CultureInfo c = CultureInfo.InvariantCulture;
			return "{"
				+ "\"frame\":" + frameIndex.ToString(c)
				+ ",\"file\":\"" + fileName + "\""
				+ ",\"realtime\":" + realtimeSeconds.ToString("F4", c)
				+ ",\"simtime\":" + simSeconds.ToString("F4", c)
				+ ",\"w\":" + ScreenWidth.ToString(c)
				+ ",\"h\":" + ScreenHeight.ToString(c)
				+ ",\"ppcx\":" + PixelsPerCellX.ToString("F6", c)
				+ ",\"ppcy\":" + PixelsPerCellY.ToString("F6", c)
				+ ",\"ox\":" + OriginPixelX.ToString("F4", c)
				+ ",\"oy\":" + OriginPixelY.ToString("F4", c)
				+ ",\"camx\":" + CameraX.ToString("F4", c)
				+ ",\"camy\":" + CameraY.ToString("F4", c)
				+ ",\"ortho\":" + OrthographicSize.ToString("F4", c)
				+ ",\"gridw\":" + Grid.WidthInCells.ToString(c)
				+ ",\"gridh\":" + Grid.HeightInCells.ToString(c)
				+ "}";
		}
	}
}
