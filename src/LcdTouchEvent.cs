namespace Haukcode.StreamDeck;

/// <summary>
/// Gesture state for an <see cref="LcdTouchEvent"/>.
/// <list type="bullet">
///   <item><see cref="Tap"/> — fired while the finger is actively touching (no explicit lift event from the device).</item>
///   <item><see cref="Hold"/> — fired continuously while the finger is resting still on the surface.</item>
///   <item><see cref="Swipe"/> — fired once when a swipe gesture completes (finger lifted after movement).
///     <see cref="LcdTouchEvent.EndX"/> and <see cref="LcdTouchEvent.EndY"/> carry the lift position;
///     <see cref="LcdTouchEvent.X"/> and <see cref="LcdTouchEvent.Y"/> carry the gesture start position.
///     Simple taps and stationary holds do not produce a Swipe event.</item>
/// </list>
/// </summary>
public enum LcdTouchEventType { Tap, Hold, Swipe }

/// <summary>Touch event from the LCD strip (Stream Deck Plus / Studio).</summary>
/// <param name="EventType">The type of touch gesture.</param>
/// <param name="X">Horizontal start position in pixels, from the left edge of the strip.</param>
/// <param name="Y">Vertical start position in pixels, from the top edge of the strip.</param>
/// <param name="EndX">Horizontal end position for <see cref="LcdTouchEventType.Swipe"/>; 0 otherwise.</param>
/// <param name="EndY">Vertical end position for <see cref="LcdTouchEventType.Swipe"/>; 0 otherwise.</param>
public sealed record LcdTouchEvent(LcdTouchEventType EventType, ushort X, ushort Y, ushort EndX = 0, ushort EndY = 0);
