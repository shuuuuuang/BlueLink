package com.bluelink.android.files

import android.content.Context
import android.webkit.WebView
import org.json.JSONArray

/** Read-only app-owned DOM measurement; no document scripts or native JS bridge. */
internal class ReadingPositionWebView(context: Context) : WebView(context) {
    var trackPosition = false
    var onHeadingChanged: (String?) -> Unit = {}
    private var pending = false
    private var released = false
    private var generation = 0
    private var loaded = false
    private val measure = Runnable {
        pending = false
        if (!released && loaded && trackPosition) {
            val expectedGeneration = generation
            evaluateJavascript(MEASURE_POSITION) { result ->
                if (!released && loaded && trackPosition && expectedGeneration == generation) {
                    val active = runCatching {
                        val values = JSONArray(result)
                        val nodes = values.getJSONArray(3)
                        val positions = (0 until nodes.length()).map { index ->
                            val node = nodes.getJSONArray(index)
                            HeadingPosition(node.getString(0), node.getDouble(1))
                        }
                        DocumentReadingPosition.current(positions, values.getDouble(0), values.getDouble(1), values.getDouble(2))
                    }.getOrNull()
                    onHeadingChanged(active)
                }
            }
        }
    }

    fun pageStarted() { generation++; loaded = false }
    fun pageFinished() { loaded = true; requestPosition() }
    fun requestPosition() {
        if (!pending && !released && trackPosition) {
            pending = true
            postDelayed(measure, 80)
        }
    }

    override fun onScrollChanged(l: Int, t: Int, oldl: Int, oldt: Int) {
        super.onScrollChanged(l, t, oldl, oldt)
        requestPosition()
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        requestPosition()
    }

    fun release() {
        released = true
        removeCallbacks(measure)
        onHeadingChanged = {}
        stopLoading()
        destroy()
    }

    companion object {
        // Constant code only. Never interpolate document text into an evaluated script.
        private const val MEASURE_POSITION = """(function() {
            const root = document.documentElement;
            const top = window.scrollY;
            const headings = Array.from(document.querySelectorAll('h1[id],h2[id],h3[id],h4[id],h5[id],h6[id]'))
                .filter(h => /^section-[0-9]+$/.test(h.id))
                .map(h => [h.id, h.getBoundingClientRect().top + top]);
            return [top, window.innerHeight, root.scrollHeight, headings];
        })()"""
    }
}
