// The wire protocol and the controller's state are the link library's now, shared with the
// sender (src/Rayxel.Link). The names the viewer has always used for them stay.
global using NetWire = Rayxel.Link.Wire;
global using PadState = Rayxel.Link.PadState;
