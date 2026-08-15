#import <Foundation/Foundation.h>
#import <Security/Security.h>

static NSMutableDictionary* DRMakeQuery(NSString* service, NSString* account) {
    return [@{
        (__bridge id)kSecClass : (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService : service ?: @"",
        (__bridge id)kSecAttrAccount : account ?: @"",
    } mutableCopy];
}

extern "C" bool Keychain_Set(const char* serviceC, const char* accountC, const char* valueC) {
    @autoreleasepool {
        NSString* service = serviceC ? [NSString stringWithUTF8String:serviceC] : @"";
        NSString* account = accountC ? [NSString stringWithUTF8String:accountC] : @"";
        NSString* value   = valueC   ? [NSString stringWithUTF8String:valueC]   : @"";

        NSMutableDictionary* query = DRMakeQuery(service, account);
        NSDictionary* attrs = @{
            (__bridge id)kSecValueData : [value dataUsingEncoding:NSUTF8StringEncoding],
            (__bridge id)kSecAttrAccessible : (__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        };

        OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query, (__bridge CFDictionaryRef)attrs);
        if (status == errSecItemNotFound) {
            [query addEntriesFromDictionary:attrs];
            status = SecItemAdd((__bridge CFDictionaryRef)query, NULL);
        }
        return (status == errSecSuccess);
    }
}

extern "C" const char* Keychain_Get(const char* serviceC, const char* accountC) {
    @autoreleasepool {
        NSString* service = serviceC ? [NSString stringWithUTF8String:serviceC] : @"";
        NSString* account = accountC ? [NSString stringWithUTF8String:accountC] : @"";

        NSMutableDictionary* query = DRMakeQuery(service, account);
        query[(__bridge id)kSecReturnData] = @YES;
        query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;

        CFTypeRef result = NULL;
        OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
        if (status != errSecSuccess) return nullptr;

        NSData* data = (__bridge_transfer NSData*)result;
        NSString* str = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
        if (!str) return nullptr;

        const char* utf8 = [str UTF8String];
        char* out = (char*)malloc(strlen(utf8) + 1);
        strcpy(out, utf8);
        return out;
    }
}

extern "C" bool Keychain_Delete(const char* serviceC, const char* accountC) {
    @autoreleasepool {
        NSString* service = serviceC ? [NSString stringWithUTF8String:serviceC] : @"";
        NSString* account = accountC ? [NSString stringWithUTF8String:accountC] : @"";

        NSMutableDictionary* query = DRMakeQuery(service, account);
        OSStatus status = SecItemDelete((__bridge CFDictionaryRef)query);
        return (status == errSecSuccess || status == errSecItemNotFound);
    }
}

extern "C" void Keychain_Free(char* p) {
    if (p) free(p);
}
