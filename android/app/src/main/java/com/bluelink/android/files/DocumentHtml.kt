package com.bluelink.android.files

import org.commonmark.parser.Parser
import org.commonmark.renderer.html.HtmlRenderer
import org.commonmark.ext.gfm.tables.TablesExtension
import org.commonmark.ext.gfm.strikethrough.StrikethroughExtension

internal data class DocumentHeading(val id: String, val title: String, val level: Int)
internal data class DocumentMarkup(val body: String, val headings: List<DocumentHeading>)

internal object DocumentHtml {
    fun escape(value: String): String = value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        .replace("\"", "&quot;").replace("'", "&#39;")

    fun body(text: String, markdown: Boolean): String = render(text, markdown).body

    fun render(text: String, markdown: Boolean): DocumentMarkup {
        if (!markdown) return DocumentMarkup("<pre class=source>${escape(text)}</pre>", emptyList())
        val extensions = listOf(TablesExtension.create(), StrikethroughExtension.create())
        val document = Parser.builder().extensions(extensions).build().parse(text)
        // Keep image descriptions visible without reading remote/local resources from a document.
        document.accept(object : org.commonmark.node.AbstractVisitor() {
            override fun visit(image: org.commonmark.node.Image) {
                val label = StringBuilder()
                image.accept(object : org.commonmark.node.AbstractVisitor() {
                    override fun visit(text: org.commonmark.node.Text) { label.append(text.literal) }
                })
                image.insertBefore(org.commonmark.node.Text("[" + label.toString().ifBlank { "image" } + "]"))
                image.unlink()
            }
        })
        val headings = mutableListOf<DocumentHeading>()
        val anchors = java.util.IdentityHashMap<org.commonmark.node.Node, String>()
        document.accept(object : org.commonmark.node.AbstractVisitor() {
            override fun visit(heading: org.commonmark.node.Heading) {
                val label = StringBuilder()
                heading.accept(object : org.commonmark.node.AbstractVisitor() {
                    override fun visit(text: org.commonmark.node.Text) { label.append(text.literal) }
                    override fun visit(code: org.commonmark.node.Code) { label.append(code.literal) }
                    override fun visit(line: org.commonmark.node.SoftLineBreak) { label.append(' ') }
                    override fun visit(line: org.commonmark.node.HardLineBreak) { label.append(' ') }
                })
                val id = "section-${headings.size + 1}"
                anchors[heading] = id
                headings += DocumentHeading(id, label.toString().trim(), heading.level)
            }
        })
        // IDs derive only from document order, so duplicate/non-Latin titles remain unambiguous.
        val renderer = HtmlRenderer.builder().extensions(extensions).escapeHtml(true).sanitizeUrls(true)
            .attributeProviderFactory { org.commonmark.renderer.html.AttributeProvider { node, _, attributes ->
                anchors[node]?.let { attributes["id"] = it }
            } }.build()
        return DocumentMarkup(renderer.render(document), headings)
    }

    fun page(body: String, dark: Boolean): String {
        val bg = if (dark) "#101620" else "#ffffff"
        val fg = if (dark) "#e6edf6" else "#182338"
        val secondary = if (dark) "#a9b8ce" else "#64748b"
        val line = if (dark) "#364357" else "#dbe3ef"
        val panel = if (dark) "#202b3a" else "#f4f7fb"
        return """<!doctype html><html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'none'">
            <style>body{background:$bg;color:$fg;font:16px/1.7 sans-serif;margin:0;padding:16px;overflow-wrap:anywhere}
            body>:first-child{margin-top:0}h1,h2,h3,h4{line-height:1.4;margin:1.1em 0 .55em}h1{font-size:1.6em}h2{font-size:1.3em;border-bottom:1px solid $line;padding-bottom:.35em}
            a{color:#3985ff}p{margin:.7em 0}blockquote{margin:1em 0;padding:.2em 1em;border-left:4px solid #3985ff;background:$panel;color:$secondary}
            pre{padding:14px;background:$panel;border-radius:10px;overflow-x:auto;white-space:pre;font:14px/1.7 monospace}
            pre.source{padding:0;background:none;white-space:pre-wrap;overflow-wrap:anywhere}code{font-family:monospace;background:$panel;padding:2px 4px;border-radius:4px}pre code{padding:0}
            table{border-collapse:collapse;display:block;overflow-x:auto;margin:1em 0}th,td{border:1px solid $line;padding:8px 12px;min-width:70px}th{background:$panel;text-align:left}
            hr{border:0;border-top:1px solid $line;margin:1.5em 0}img{display:none}ul,ol{padding-left:1.5em}</style></head><body>$body</body></html>"""
    }
}
