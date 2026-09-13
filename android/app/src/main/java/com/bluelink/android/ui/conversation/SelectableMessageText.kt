package com.bluelink.android.ui.conversation

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.text.selection.LocalTextSelectionColors
import androidx.compose.foundation.text.selection.TextSelectionColors
import androidx.compose.material3.MaterialTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.LayoutCoordinates
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalTextToolbar
import androidx.compose.ui.platform.TextToolbar
import androidx.compose.ui.platform.TextToolbarStatus
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.*
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupPositionProvider
import androidx.compose.ui.window.PopupProperties
import com.bluelink.android.R
import com.bluelink.android.ui.devices.DeviceColors

/** Selection owns the long press; message actions remain available from the selection toolbar. */
@Composable
internal fun SelectableMessageText(controller: MessageSelectionController, outgoing: Boolean, more: () -> Unit,
                                   text: String, textStyle: TextStyle, modifier: Modifier = Modifier) {
    var value by remember(text) { mutableStateOf(TextFieldValue(text)) }
    val toolbar = remember { MessageTextToolbar() }
    val latestMore by rememberUpdatedState(more)
    val focusManager = LocalFocusManager.current
    val view = LocalView.current
    val region = remember { MessageSelectionRegion() }
    val foreground = MaterialTheme.colorScheme.onPrimary
    val colors = TextSelectionColors(
        handleColor = Color(0xFFA8D1FF),
        backgroundColor = if (!outgoing) DeviceColors.Blue.copy(alpha = 0.35f)
            else if (foreground.luminance() > 0.5f) Color.Black.copy(alpha = 0.35f)
            else Color.White.copy(alpha = 0.55f))
    DisposableEffect(toolbar, controller, region) { onDispose { toolbar.hide(); controller.release(region) } }
    // Exactly one child in the message Column: popup visibility cannot add another spaced item.
    Box {
        CompositionLocalProvider(LocalTextToolbar provides toolbar, LocalTextSelectionColors provides colors) {
            // A message is one text layout. Read-only field selection keeps resolving the nearest
            // line/character outside its bounds; multi-widget selection snaps to the whole text.
            BasicTextField(value = value, onValueChange = { next ->
                if (next.text == text) value = next.copy(composition = null)
            }, readOnly = true, textStyle = textStyle, cursorBrush = SolidColor(Color.Transparent),
                modifier = modifier.onGloballyPositioned { region.coordinates = it }.onFocusChanged {
                    if (it.hasFocus) controller.activate(region)
                    else {
                        controller.release(region)
                        toolbar.hide()
                        value = value.copy(selection = TextRange.Zero, composition = null)
                    }
                })
        }
        if (toolbar.status == TextToolbarStatus.Shown) {
            val gap = with(LocalDensity.current) { 8.dp.roundToPx() }
            val position = remember(toolbar.rect, gap, view) { object : PopupPositionProvider {
                override fun calculatePosition(anchorBounds: IntRect, windowSize: IntSize,
                    layoutDirection: LayoutDirection, popupContentSize: IntSize): IntOffset {
                    // Selection rectangles are relative to the Compose view; Popup positions use window coordinates.
                    val origin = IntArray(2)
                    view.getLocationInWindow(origin)
                    return IntOffset(
                        (origin[0] + toolbar.rect.left.toInt()).coerceIn(0, (windowSize.width - popupContentSize.width).coerceAtLeast(0)),
                        (origin[1] + toolbar.rect.top.toInt() - popupContentSize.height - gap)
                            .coerceIn(0, (windowSize.height - popupContentSize.height).coerceAtLeast(0)))
                }
            } }
            Popup(popupPositionProvider = position, onDismissRequest = toolbar::hide,
                properties = PopupProperties(focusable = false, dismissOnClickOutside = false)) {
                Surface(shape = RoundedCornerShape(10.dp), color = DeviceColors.Surface, shadowElevation = 6.dp) {
                    Row {
                        toolbar.copy?.let { action -> TextButton(onClick = { action(); toolbar.hide() }) {
                            Text(stringResource(android.R.string.copy), color = DeviceColors.Blue)
                        } }
                        toolbar.selectAll?.let { action -> TextButton(onClick = action) {
                            Text(stringResource(android.R.string.selectAll), color = DeviceColors.Blue)
                        } }
                        TextButton(onClick = { focusManager.clearFocus(force = true); toolbar.hide(); latestMore() }) {
                            Text(stringResource(R.string.content_more_actions), color = DeviceColors.Blue)
                        }
                    }
                }
            }
        }
    }
}

private class MessageTextToolbar : TextToolbar {
    override var status by mutableStateOf(TextToolbarStatus.Hidden)
        private set
    var rect by mutableStateOf(Rect.Zero)
        private set
    var copy: (() -> Unit)? by mutableStateOf(null)
        private set
    var selectAll: (() -> Unit)? by mutableStateOf(null)
        private set
    override fun showMenu(rect: Rect, onCopyRequested: (() -> Unit)?, onPasteRequested: (() -> Unit)?,
        onCutRequested: (() -> Unit)?, onSelectAllRequested: (() -> Unit)?) {
        this.rect = rect
        copy = onCopyRequested
        selectAll = onSelectAllRequested
        status = TextToolbarStatus.Shown
    }
    override fun hide() { status = TextToolbarStatus.Hidden }
}


internal class MessageSelectionRegion {
    var coordinates: LayoutCoordinates? = null
}

/** Observe outside presses without consuming them or intercepting selection-handle drags. */
internal class MessageSelectionController(private val clearFocus: () -> Unit) {
    var surface: LayoutCoordinates? = null
    private var active: MessageSelectionRegion? = null
    fun activate(region: MessageSelectionRegion) { active = region }
    fun release(region: MessageSelectionRegion) { if (active === region) active = null }
    fun press(position: Offset) {
        val selected = active ?: return
        val parent = surface?.takeIf { it.isAttached }
        val child = selected.coordinates?.takeIf { it.isAttached }
        if (parent == null || child == null || !parent.localBoundingBoxOf(child, clipBounds = false).contains(position)) {
            active = null
            clearFocus()
        }
    }
}

internal fun Modifier.messageSelectionSurface(controller: MessageSelectionController): Modifier =
    onGloballyPositioned { controller.surface = it }.pointerInput(controller) {
        awaitEachGesture {
            val down = awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
            controller.press(down.position)
        }
    }
