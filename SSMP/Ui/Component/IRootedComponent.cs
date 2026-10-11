using UnityEngine;

namespace SSMP.Ui.Component;

/// <summary>
/// A component drawn on a GameObject of its own without deriving from <see cref="Component"/>, like the panels that
/// take the place of a tab's content in the connect menu. Its object is moved into the menu's background along with
/// every other component of its group (<see cref="ComponentGroup.ReparentComponents"/>).
///
/// Left on the canvas, such a panel kept the place it had when the menu was built, while everything moved into the
/// background was spread out with the background once the canvas was scaled to the screen. On a screen taller than
/// 1080 pixels that put the header of the panel over the row of tabs.
/// </summary>
internal interface IRootedComponent : IComponent {
    /// <summary>
    /// The object the component is drawn on.
    /// </summary>
    GameObject Root { get; }
}
