plugins {
    base
}

tasks.register("verify") {
    group = "verification"
    description = "Runs all platform-independent BTX checks."
    dependsOn(":protocol-core:verify")
}

