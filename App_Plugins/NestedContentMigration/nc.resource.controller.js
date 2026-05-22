angular.module('umbraco.resources').factory('ncResource',
    function ($http, umbRequestHelper) {
        return {
            getAll: function () {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/migration/audit"),
                    "Failed to retrieve audit data");
            },

            update: function (data) {
                return umbRequestHelper.resourcePromise(
                    $http.post("api/migration/update", data),
                    "Failed to update property");
            },

            schemaCheck: function () {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/migration/schemacheck"),
                    "Failed to retrieve schema check");
            },

            schemaFix: function () {
                return umbRequestHelper.resourcePromise(
                    $http.post("api/migration/schemafix", {}),
                    "Failed to fix schema");
            },

            mediaPickerAudit: function () {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/migration/mediapickeraudit"),
                    "Failed to retrieve media picker audit");
            },

            contentCheck: function (docTypeAlias, ncAlias, blAlias) {
                return umbRequestHelper.resourcePromise(
                    $http.get("api/migration/contentcheck", {
                        params: { docTypeAlias: docTypeAlias, ncAlias: ncAlias, blAlias: blAlias }
                    }),
                    "Failed to retrieve content check");
            },

            mediaPickerFix: function () {
                return umbRequestHelper.resourcePromise(
                    $http.post("api/migration/mediapickerfix", {}),
                    "Failed to fix media pickers");
            }
        };
    }
);