package com.bluelink.core;

import java.nio.file.Path;
import java.util.Locale;
import java.util.Set;

public final class SafePaths {
    private static final Set<String> WINDOWS_RESERVED = Set.of(
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9");

    private SafePaths() {}

    public static Path resolve(Path managedRoot, String relativePath) {
        if (relativePath == null || relativePath.isBlank()) throw new IllegalArgumentException("Empty relative path");
        String slash = relativePath.replace('\\', '/');
        if (slash.startsWith("/") || slash.startsWith("//") || slash.matches("^[A-Za-z]:.*")
                || slash.startsWith("\\\\?\\") || slash.indexOf('\0') >= 0) {
            throw new IllegalArgumentException("Absolute or device path is forbidden");
        }
        for (String component : slash.split("/", -1)) {
            if (component.isBlank() || component.equals(".") || component.equals("..")) {
                throw new IllegalArgumentException("Unsafe path component");
            }
            if (component.endsWith(".") || component.endsWith(" ")) throw new IllegalArgumentException("Unsafe Windows filename");
            String stem = component.split("\\.", 2)[0].toUpperCase(Locale.ROOT);
            if (WINDOWS_RESERVED.contains(stem)) throw new IllegalArgumentException("Reserved Windows filename");
        }
        Path root = managedRoot.toAbsolutePath().normalize();
        Path result = root.resolve(slash.replace('/', managedRoot.getFileSystem().getSeparator().charAt(0))).normalize();
        if (!result.startsWith(root)) throw new IllegalArgumentException("Path escapes managed root");
        return result;
    }
}

