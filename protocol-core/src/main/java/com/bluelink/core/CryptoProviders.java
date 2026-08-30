package com.bluelink.core;

import javax.crypto.Cipher;
import javax.crypto.KeyAgreement;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.PrivateKey;
import java.security.Provider;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.Security;
import java.security.Signature;
import java.security.spec.NamedParameterSpec;
import java.security.spec.PKCS8EncodedKeySpec;
import java.security.spec.X509EncodedKeySpec;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

final class CryptoProviders {
    private static volatile Provider preferredProvider;

    private CryptoProviders() {}

    static void prefer(Provider provider) {
        if (provider == null) throw new IllegalArgumentException("Provider is required");
        if (isAndroidKeyStore(provider)) throw new IllegalArgumentException("AndroidKeyStore cannot be used for BTX keys");
        preferredProvider = provider;
    }

    static String preferredDescription() {
        Provider provider = preferredProvider;
        if (provider == null) return "system JCA providers";
        // Android's java.security.Provider does not expose the Java SE 9
        // getVersionStr() method. Provider.id version is populated by the
        // Provider base class and can be read through the API-level-1 Map API.
        try {
            Object version = provider.get("Provider.id version");
            String value = version == null ? "" : version.toString().trim();
            return value.isEmpty() ? provider.getName() : provider.getName() + " " + value;
        } catch (RuntimeException | LinkageError ignored) {
            // Provider metadata is diagnostic-only and must never make the
            // cryptographic runtime unavailable.
            return provider.getName();
        }
    }

    static GeneratedKeyPair generateX25519() throws GeneralSecurityException {
        return generate(new String[] { "X25519", "XDH" }, NamedParameterSpec.X25519);
    }

    static GeneratedKeyPair generateEd25519() throws GeneralSecurityException {
        return generate(new String[] { "Ed25519", "EdDSA" }, NamedParameterSpec.ED25519);
    }

    static GeneratedKeyPair restoreEd25519(byte[] privateKey, byte[] publicKey)
            throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(null)) {
            for (String algorithm : new String[] { "Ed25519", "EdDSA" }) {
                try {
                    KeyFactory factory = KeyFactory.getInstance(algorithm, provider);
                    KeyPair pair = new KeyPair(
                            factory.generatePublic(new X509EncodedKeySpec(publicKey)),
                            factory.generatePrivate(new PKCS8EncodedKeySpec(privateKey)));
                    return new GeneratedKeyPair(pair, provider);
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        throw unavailable("Ed25519 KeyFactory", failures);
    }

    static byte[] signEd25519(PrivateKey privateKey, Provider preferred, byte[] content)
            throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(preferred)) {
            for (String algorithm : new String[] { "Ed25519", "EdDSA" }) {
                try {
                    Signature signature = Signature.getInstance(algorithm, provider);
                    signature.initSign(privateKey);
                    signature.update(content);
                    return signature.sign();
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        throw unavailable("Ed25519 Signature", failures);
    }

    static void verifyEd25519(byte[] publicKey, byte[] content, byte[] signed)
            throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        boolean invalidSignature = false;
        for (Provider provider : providers(null)) {
            for (String algorithm : new String[] { "Ed25519", "EdDSA" }) {
                try {
                    KeyFactory factory = KeyFactory.getInstance(algorithm, provider);
                    PublicKey decoded = factory.generatePublic(new X509EncodedKeySpec(publicKey));
                    Signature signature = Signature.getInstance(algorithm, provider);
                    signature.initVerify(decoded);
                    signature.update(content);
                    if (!signature.verify(signed)) {
                        invalidSignature = true;
                        continue;
                    }
                    return;
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        if (invalidSignature) throw new GeneralSecurityException("Invalid identity signature");
        throw unavailable("Ed25519 verification", failures);
    }

    static PublicKey decodeX25519(byte[] encoded, Provider preferred) throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(preferred)) {
            for (String algorithm : new String[] { "X25519", "XDH" }) {
                try {
                    return KeyFactory.getInstance(algorithm, provider)
                            .generatePublic(new X509EncodedKeySpec(encoded));
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        throw unavailable("X25519 KeyFactory", failures);
    }

    static KeyAgreement x25519Agreement(PrivateKey privateKey, Provider preferred)
            throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(preferred)) {
            for (String algorithm : new String[] { "X25519", "XDH" }) {
                try {
                    KeyAgreement agreement = KeyAgreement.getInstance(algorithm, provider);
                    agreement.init(privateKey);
                    return agreement;
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        throw unavailable("X25519 KeyAgreement", failures);
    }

    static Cipher chacha20Poly1305() throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(null)) {
            for (String algorithm : new String[] { "ChaCha20-Poly1305", "ChaCha20/Poly1305/NoPadding" }) {
                try {
                    return Cipher.getInstance(algorithm, provider);
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                }
            }
        }
        throw unavailable("ChaCha20-Poly1305 Cipher", failures);
    }

    private static GeneratedKeyPair generate(String[] algorithms, NamedParameterSpec parameters)
            throws GeneralSecurityException {
        List<Throwable> failures = new ArrayList<>();
        for (Provider provider : providers(null)) {
            for (String algorithm : algorithms) {
                try {
                    KeyPairGenerator generator = KeyPairGenerator.getInstance(algorithm, provider);
                    generator.initialize(parameters, new SecureRandom());
                    return new GeneratedKeyPair(generator.generateKeyPair(), provider);
                } catch (GeneralSecurityException | RuntimeException failure) {
                    failures.add(failure);
                    // Algorithm-specific generators already imply the curve. Some
                    // Android providers reject NamedParameterSpec but can generate
                    // the correct key without explicit initialization.
                    try {
                        KeyPairGenerator generator = KeyPairGenerator.getInstance(algorithm, provider);
                        return new GeneratedKeyPair(generator.generateKeyPair(), provider);
                    } catch (GeneralSecurityException | RuntimeException fallbackFailure) {
                        failures.add(fallbackFailure);
                    }
                }
            }
        }
        throw unavailable(algorithms[0] + " KeyPairGenerator", failures);
    }

    private static List<Provider> providers(Provider operationPreferred) {
        Map<String, Provider> ordered = new LinkedHashMap<>();
        if (operationPreferred != null) ordered.put(operationPreferred.getName(), operationPreferred);
        Provider configured = preferredProvider;
        if (configured != null) ordered.put(configured.getName(), configured);
        for (Provider provider : Security.getProviders()) ordered.putIfAbsent(provider.getName(), provider);
        ordered.values().removeIf(CryptoProviders::isAndroidKeyStore);
        return new ArrayList<>(ordered.values());
    }

    private static GeneralSecurityException unavailable(String operation, List<Throwable> failures) {
        GeneralSecurityException result = new GeneralSecurityException(
                "No usable software provider for " + operation + " (preferred=" + preferredDescription() + ")");
        failures.forEach(result::addSuppressed);
        return result;
    }

    private static boolean isAndroidKeyStore(Provider provider) {
        return provider.getName().toLowerCase(Locale.ROOT).contains("androidkeystore");
    }

    record GeneratedKeyPair(KeyPair keyPair, Provider provider) {}
}
