plugins {
    `java-library`
}

group = "com.bluelink"
version = "0.1.0"

java {
    toolchain.languageVersion.set(JavaLanguageVersion.of(17))
}

tasks.register<JavaExec>("verify") {
    group = "verification"
    description = "Runs dependency-free BTX protocol and transfer checks."
    classpath = sourceSets.test.get().runtimeClasspath
    mainClass.set("com.bluelink.core.VerificationMain")
    dependsOn(tasks.testClasses)
}

