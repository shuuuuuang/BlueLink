pluginManagement {
    repositories {
        if (System.getenv("CI") != "true") {
            maven("https://repo.huaweicloud.com/repository/maven/")
        }
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        if (System.getenv("CI") != "true") {
            maven("https://repo.huaweicloud.com/repository/maven/")
        }
        google()
        mavenCentral()
    }
}
rootProject.name = "BlueLinkAndroid"
include(":app")
include(":protocol-core")
project(":protocol-core").projectDir = file("../protocol-core")
