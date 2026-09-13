package com.bluelink.android.files

import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.Locale

class FileTypeCatalogTest {
    @Test fun sharedClassificationCasesMatchOnBothPlatforms() {
        val stream = javaClass.classLoader!!.getResourceAsStream("file-icon-cases.tsv")!!
        stream.bufferedReader().useLines { lines ->
            lines.drop(1).filter { it.isNotBlank() }.forEach { line ->
                val cells = line.split('\t')
                assertEquals(cells[0], cells[2], FileTypeCatalog.classify(cells[0], cells[1]))
            }
        }
    }
    @Test fun fileTypesDoNotDependOnDeviceLocale() {
        val previous = Locale.getDefault()
        try {
            Locale.setDefault(Locale("tr", "TR"))
            assertEquals("file-config", FileTypeCatalog.classify("SETTINGS.INI"))
            assertEquals("file-disk-image", FileTypeCatalog.classify("DISK.ISO"))
            assertEquals("file", FileTypeCatalog.classify(null, null))
        } finally { Locale.setDefault(previous) }
    }
}
