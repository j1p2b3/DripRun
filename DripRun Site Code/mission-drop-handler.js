import { Permissions, webMethod } from 'wix-web-module';
import { fetch } from 'wix-fetch';
import { files } from 'wix-media.v2';
import { elevate } from 'wix-auth';

export const getMissionInfo = webMethod(Permissions.Anyone, async (infoType, jwtToken, test) => {

    if (infoType != '' && jwtToken != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/${infoType}`, {
                method: 'GET',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.error('getDashboardInfo', test, err);
            return null;
        };
    };

});

export const toggleMissionStatus = webMethod(Permissions.Anyone, async (missionId, jwtToken) => {

    if (missionId != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/missions/${missionId}/toggle-status`, {
                method: 'PATCH',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.error('toggleMissionStatus', err);
            return null;
        };
    };

});

export const createMission = webMethod(Permissions.Anyone, async (missionInfo, jwtToken) => {

    if (missionInfo != '') { 
        try {
            // convert wix blob to url
            const elevatedGenerateFileDonwloadUrl = elevate(files.generateFileDownloadUrl);
            const missionImgUrl = await elevatedGenerateFileDonwloadUrl(missionInfo[4]);

            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/missions`, {
                method: 'POST',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`,
                    'Content-Type':  'application/json'
                },
                body: JSON.stringify({
                    // Required
                    'Name':            missionInfo[0],
                    // Optional
                    'Description':     missionInfo[1],
                    'LocationName':    missionInfo[2],
                    // Booleans
                    'isVisible':       missionInfo[3],
                    // Required
                    'missionImageUrl': missionImgUrl.downloadUrls[0].url,
                    'ImgName':         missionInfo[5]
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };

            // remove mission image form wix storage because wix automatically saves it
            const elevatedBulkDeleteFiles = elevate(files.bulkDeleteFiles);
            await elevatedBulkDeleteFiles([missionInfo[4]], { permanent: true });
        }
        catch (err) {
            console.error('createMission', err);
            return null;
        };
    };

});

export const deleteMission = webMethod(Permissions.Anyone, async (missionId, jwtToken) => {

    if (missionId != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/missions/${missionId}`, {
                method: 'DELETE',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };
        }
        catch (err) {
            console.error('deleteMission', err);
            return null;
        };
    };

});

export const getCouponLocation = webMethod(Permissions.Anyone, async (couponId, jwtToken) => {

    if (couponId != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/coupons/${couponId}/locations`, {
                method: 'GET',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.error('getCouponLocation', err);
            return null;
        };
    };

});

export const createCoupon = webMethod(Permissions.Anyone, async (couponInfo, jwtToken) => {
    if (couponInfo != '') { 
        try {
            // convert wix blob to url
            const elevatedGenerateFileDonwloadUrl = elevate(files.generateFileDownloadUrl);
    
            let modelUrl = couponInfo[9];

        // 🔥 If it's a Wix file → convert it
        if (modelUrl.startsWith("wix:")) {
            const elevatedGenerateFileDonwloadUrl = elevate(files.generateFileDownloadUrl);
            const res = await elevatedGenerateFileDonwloadUrl(modelUrl);

            if (!res?.downloadUrls?.[0]?.url) {
                throw new Error("Model image URL generation failed");
            }

            modelUrl = res.downloadUrls[0].url;
        }

            let mainImageUrl = null;
            if (couponInfo[10]) {
                mainImageUrl = await elevatedGenerateFileDonwloadUrl(couponInfo[10]);
            }
            let logoImageUrl = null;
            if (couponInfo[11]) {
                logoImageUrl = await elevatedGenerateFileDonwloadUrl(couponInfo[11]);
            }

            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/coupons/create-with-locations`, {
                method: 'POST',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`,
                    'Content-Type':  'application/json'
                },
                body: JSON.stringify({
                MissionId: couponInfo[0],

                DropName: couponInfo[1],
                ShopifyLink: couponInfo[2],
                TosLink: couponInfo[3],

                Rarity: couponInfo[4],
                CouponOrCollectible: couponInfo[5],

                Latitudes: couponInfo[6],
                Longitudes: couponInfo[7],
                LocationNames: couponInfo[8],

                ModelFileUrl: modelUrl,

                // ✅ SAFE optional main image
                MainImageUrl: mainImageUrl ? mainImageUrl.downloadUrls[0].url : null,

                // ✅ NEW
                LogoImageUrl: logoImageUrl ? logoImageUrl.downloadUrls[0].url : null,
                Description: couponInfo[12] || null
            })
            });
    
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };
    
            // cleanup uploads
            const elevatedBulkDeleteFiles = elevate(files.bulkDeleteFiles);
            const filesToDelete = [];
            // Model is always present
            filesToDelete.push(couponInfo[9]);
            // Optional files
            if (couponInfo[10]) filesToDelete.push(couponInfo[10]); // main
            if (couponInfo[11]) filesToDelete.push(couponInfo[11]); // logo
            await elevatedBulkDeleteFiles(filesToDelete, { permanent: true });
        }
        catch (err) {
            console.error('createCoupon', err);
            return null;
        };
    };
});
    

export const deleteCoupon = webMethod(Permissions.Anyone, async (couponId, jwtToken) => {

    if (couponId != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin/coupons/${couponId}`, {
                method: 'DELETE',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' ' + httpResponseMsg);
            };
        }
        catch (err) {
            console.error('deleteCoupon', err);
            return null;
        };
    };

});

export const deleteUploadedImg = webMethod(Permissions.Anyone, async (imageUrls) => {

    // remove mission image form wix storage because wix automatically saves it
    const elevatedBulkDeleteFiles = elevate(files.bulkDeleteFiles);
    await elevatedBulkDeleteFiles(imageUrls, { permanent: true });

});
