plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.kapt")
}

val productVersion = rootProject.file("../VERSION").readText().trim()

android {
    namespace = "com.bluelink.android"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.bluelink.android"
        minSdk = 33
        targetSdk = 34
        versionCode = 12
        versionName = productVersion
    }

    // A physical device may already use the developer's debug key. Sandboxed
    // JVMs have a different user.home, so allow that same key without copying it.
    providers.environmentVariable("BLUELINK_DEBUG_KEYSTORE").orNull?.let { path ->
        signingConfigs.getByName("debug") {
            storeFile = file(path)
            storePassword = "android"
            keyAlias = "androiddebugkey"
            keyPassword = "android"
        }
    }

    // ABI-specific artifacts are opt-in so ordinary IDE/debug builds keep their paths.
    val splitApks = providers.gradleProperty("bluelinkSplitApks").orNull == "true"
    val supportedAbis = listOf("armeabi-v7a", "arm64-v8a", "x86", "x86_64")
    val requestedAbis = providers.gradleProperty("bluelinkAbis").orNull
        ?.split(",")?.filter { it.isNotBlank() } ?: supportedAbis
    require(requestedAbis.isNotEmpty() && requestedAbis.all { it in supportedAbis }) {
        "bluelinkAbis must contain only: ${supportedAbis.joinToString()}"
    }
    splits {
        abi {
            isEnable = splitApks
            reset()
            include(*requestedAbis.toTypedArray())
            isUniversalApk = true
        }
    }

    // Language can change inside BlueLink without a Play Store language download.
    bundle { language { enableSplit = false } }
    buildFeatures { compose = true; buildConfig = true }
    composeOptions { kotlinCompilerExtensionVersion = "1.5.8" }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    sourceSets["main"].assets.srcDir("../../design/brand/final")
    sourceSets["main"].res.srcDir("../../shared/file-icons/res")
    sourceSets["test"].resources.srcDir("../../shared/file-icons/tests")
    sourceSets["test"].resources.srcDir("../../shared/thumbnail/tests")
    sourceSets["main"].assets.srcDir("../../shared/file-icons/assets")
}

dependencies {
    implementation(project(":protocol-core"))
    implementation("org.conscrypt:conscrypt-android:2.6.0")
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.activity:activity-compose:1.8.2")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.7.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.7.0")
    implementation("androidx.room:room-runtime:2.6.1")
    implementation("androidx.room:room-ktx:2.6.1")
    kapt("androidx.room:room-compiler:2.6.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")
    implementation(platform("androidx.compose:compose-bom:2024.02.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material")
    implementation("androidx.compose.material:material-icons-extended")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
}

kapt {
    arguments {
        arg("room.schemaLocation", "$projectDir/schemas")
    }
}

// Generate the in-app attribution list from the exact runtime artifacts, including
// transitive dependencies. The cached publisher POM supplies each license name.
listOf("debug", "release").forEach { variant ->
    val output = layout.buildDirectory.dir("generated/license-assets/$variant")
    val generate = tasks.register("generate${variant.replaceFirstChar { it.uppercase() }}LicenseAssets") {
        val runtime = configurations.named("${variant}RuntimeClasspath")
        inputs.files(runtime)
        outputs.dir(output)
        doLast {
            val factory = javax.xml.parsers.DocumentBuilderFactory.newInstance().apply {
                setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
            }
            val root = gradle.gradleUserHomeDir.resolve("caches/modules-2/files-2.1")
            fun licensesFor(group: String, name: String, version: String, depth: Int = 0): List<String> {
                check(depth < 12) { "Cyclic or excessive POM inheritance for $group:$name:$version" }
                val pom = root.resolve("$group/$name/$version").walkTopDown().firstOrNull { it.extension == "pom" }
                    ?: error("Missing cached publisher POM for $group:$name:$version")
                val doc = factory.newDocumentBuilder().parse(pom)
                val licenses = doc.getElementsByTagName("license")
                val names = (0 until licenses.length).map { index ->
                    val element = licenses.item(index) as org.w3c.dom.Element
                    element.getElementsByTagName("name").item(0)?.textContent.orEmpty()
                }.filter { it.isNotBlank() }.distinct()
                if (names.isNotEmpty()) return names
                val parent = doc.getElementsByTagName("parent").item(0) as? org.w3c.dom.Element
                    ?: error("Publisher license missing for $group:$name:$version")
                fun field(key: String) = parent.getElementsByTagName(key).item(0).textContent
                return licensesFor(field("groupId"), field("artifactId"), field("version"), depth + 1)
            }
            val rows = runtime.get().resolvedConfiguration.resolvedArtifacts
                .filter { it.id.componentIdentifier is org.gradle.api.artifacts.component.ModuleComponentIdentifier }
                .sortedBy { it.moduleVersion.id.toString() }.map { artifact ->
                    val id = artifact.moduleVersion.id
                    mapOf("name" to "${id.group}:${id.name}", "version" to id.version,
                        "licenses" to licensesFor(id.group, id.name, id.version))
                }.distinctBy { it["name"] }
            val target = output.get().asFile.resolve("licenses/dependencies.json")
            target.parentFile.mkdirs()
            target.writeText(groovy.json.JsonOutput.prettyPrint(groovy.json.JsonOutput.toJson(rows)))
        }
    }
    android.sourceSets.getByName(variant).assets.srcDir(output)
    tasks.matching { it.name == "pre${variant.replaceFirstChar { it.uppercase() }}Build" }.configureEach { dependsOn(generate) }
}
