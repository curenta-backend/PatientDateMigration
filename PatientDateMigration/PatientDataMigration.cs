// See https://aka.ms/new-console-template for more information
using Domain.Entities;
using Domain.Interfaces.ExternalClients;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Patients.Models;
using PatientsCoreAPI.Infrastructure;
using PatientsCoreAPI.Infrastructure.Models;
using System.Globalization;
using static CurentaDomain.Enums.EnumsCollection;

internal class PatientDataMigration
{
    private PatientContext _oldPatientDbContext;
    private IFacilityClient _facilityClient;
    public PatientDataMigration(PatientContext oldPatientDbContext, Domain.Interfaces.ExternalClients.IFacilityClient facilityClient)
    {
        _oldPatientDbContext = oldPatientDbContext;
        _facilityClient = facilityClient;
    }

    internal async Task MigrateAsync()
    {
        try 
        {
            //var patient = dbContext.Patient.ToList();

            var patients = _oldPatientDbContext.Patient
                .Include(p => p.InsuranceCard)
                .Include(p => p.Orders)
                .Include(p => p.PatientAddresses)
                .Include(p => p.PatientAllergies)
                .Include(p => p.PatientFiles)
                .Include(p => p.RefillRequestMain)
                .Include(p => p.TransferRequest)
                .Include(p => p.PatientNotes)
                .Include(p => p.PatientFiles)
                .Include(p => p.PatientResidential).ThenInclude(p => p.Resuscitation)
                .AsEnumerable();

            foreach (var patient in patients)
            {
                await MigratePatient(patient);
            }

        }
        catch (Exception e) 
        {
            Console.WriteLine("Error: " + e.Message);
        }
    }

    private async Task MigratePatient(PatientsCoreAPI.Infrastructure.Models.Patient patient)
    {
        try
        {
            var newPatient = new Domain.Entities.Patient();

            //patient basic data with addresses and place of service
            var gender = new Domain.Entities.Gender();
            Enum.TryParse(patient.Gender, out gender);

            var basicInfoResult = Domain.Entities.PatientBasicInfo.Create(
                patient.Fname,
                patient.Lname,
                patient.Email,
                patient.Phonenumber,
                DateOnly.FromDateTime(DateTime.Parse(patient.Dob)),
                gender
                );
            if (basicInfoResult.IsFailure)
                throw new Exception(basicInfoResult.Error);

            var addressess = new List<Domain.Entities.Address>();
            foreach (var address in patient.PatientAddresses)
            {
                var addressType = new AddressType();
                if (address.AddressType.ToLower().Contains("assisted"))
                    addressType = AddressType.AssistedLivingFacility;
                else if (address.AddressType.ToLower().Contains("board"))
                    addressType = AddressType.BoardAndCare;
                else if (address.AddressType.ToLower().Contains("residential"))
                    addressType = AddressType.Residential;
                else if (address.AddressType.ToLower().Contains("skilled"))
                    addressType = AddressType.SkilledNursingFacility;
                else if (address.AddressType.ToLower().Contains("other"))
                    addressType = AddressType.Other;

                var newAddressCreateResult = Domain.Entities.Address.Create(
                    address.Address,
                    address.Address,
                    address.Street,
                    address.City,
                    address.State,
                    address.ZipCode,
                    "USA",//todo
                    addressType,
                    address.Lng,
                    address.Lat,
                    address.Address == patient.DeliveryAddress ? true : false,
                    false,//todo
                    false,//todo
                    address.IsDefault != null && address.IsDefault == true ? true : false
                    );

                if (newAddressCreateResult.IsFailure)
                    throw new Exception(newAddressCreateResult.Error);

                addressess.Add(newAddressCreateResult.Value);
            }

            var patientStatus = new Domain.Entities.PatientStatus();
            Enum.TryParse(patient.PatientStatus, out patientStatus);

            long? newPatientFacilityId = null;

            if (patient.FacilityIdRef == null && patient.Facility == null)
            {
                var createPtientResult = Domain.Entities.Patient.CreateRetailPatient(basicInfoResult.Value, addressess, patientStatus);
                if (createPtientResult.IsFailure)
                    throw new Exception(createPtientResult.Error);
                newPatient = createPtientResult.Value;
            }
            else
            {
                newPatientFacilityId = patient.FacilityIdRef != null && patient.FacilityIdRef != 0 ? (long)patient.FacilityIdRef : (long)patient.Facility.Id;
                var facilityCreateResult = Domain.Entities.PlaceOfServiceDetails.Create(
                        (long)newPatientFacilityId,
                        patient.PatientResidential != null && patient.PatientResidential.WingId != null ? patient.PatientResidential.WingId.ToString() : null,
                        patient.PatientResidential != null && !string.IsNullOrEmpty(patient.PatientResidential.Room) ? patient.PatientResidential.Room : null,
                        patient.NurseIdRef != null ? patient.NurseIdRef.ToString():null,
                        LocationOfService.Facility//todo is this correct?
                    );
                if (facilityCreateResult.IsFailure)
                    throw new Exception(facilityCreateResult.Error);

                var createPtientResult = Domain.Entities.Patient.CreateFacilityPatient(basicInfoResult.Value, addressess, patientStatus, facilityCreateResult.Value);
                if (createPtientResult.IsFailure)
                    throw new Exception(createPtientResult.Error);
                newPatient = createPtientResult.Value;
            };

            //personal info
            string? resuscitation = null;
            if (patient.PatientResidential != null && patient.PatientResidential.Resuscitation != null)
                resuscitation = patient.PatientResidential.Resuscitation.ResuscitationName;
            else if (patient.PatientResidential.ResuscitationId != null)
                resuscitation = ((EResuscitation)Enum.ToObject(typeof(EResuscitation), patient.PatientResidential.ResuscitationId)).ToString();
            else if (patient.PatientResidential.ResuscitationDisplayValue != null)
                resuscitation = patient.PatientResidential.ResuscitationDisplayValue.ToString();

            var personalInfoResult = PatientPersonalInformation.Create(
                    patient.SocialSecurityNumb,
                    patient.Mrnumber,
                    patient.MainDiagnosis,
                    patient.PatientResidential?.Diet,
                    patient.PatientAllergies != null ? patient.PatientAllergies.Select(a => a.AllergyDesc).ToList() : null,
                    resuscitation
                    );
            if (personalInfoResult.IsFailure)
                throw new Exception(personalInfoResult.Error);
            newPatient.UpdatePersonalInfo(personalInfoResult.Value);

            //other fields
            //todo external id
            //todo bubble pack
            if (!string.IsNullOrEmpty(patient.DeliveryNote))
                newPatient.SetDeliveryNote(patient.DeliveryNote);
            if (!string.IsNullOrEmpty(patient.Comments))
                newPatient.SetComment(patient.Comments);
            if (!string.IsNullOrEmpty(patient.ProfilePicPath))
                newPatient.UpdateProfilePic(patient.ProfilePicPath);

            //files
            foreach (var file in patient.PatientFiles)
            {
                var createDocumentResult = Domain.ValueObjects.Document.Create(file.AzureFilePath);
                if (createDocumentResult.IsFailure)
                    throw new Exception(createDocumentResult.Error);
                newPatient.AddDocument(createDocumentResult.Value);
            }

            //notes
            foreach (var note in patient.PatientNotes)
            {
                var createNoteResult = Note.Create(note.Title, note.Body);
                if (createNoteResult.IsFailure)
                    throw new Exception(createNoteResult.Error);
                newPatient.AddNote(createNoteResult.Value);
            }

            //medications
            var patientMedications = _oldPatientDbContext.PatientMedication
                .Include(p=>p.AdminHours)
                .Where(p => p.PatientIdRef == patient.PatientId)
                .ToList();

            foreach (var medication in patientMedications)
            {
                var medicalInfoResult = PatientMedicationMedicalInfo.Create(
                        medication.TherapCode,
                        medication.HowtoUse,
                        medication.MedName,
                        medication.Ndc,
                        medication.DispensableGenericId,
                        medication.DispensableGenericDesc,
                        medication.DispensableDrugId,
                        medication.DispensableDrugDesc,
                        medication.DispensableDrugTallManDesc,
                        medication.MedStrength,
                        medication.MedStrengthUnit,
                        medication.Indication,
                        medication.IscomfortKit,
                        medication.ComfortKitType,
                        medication.ComfortKit,
                        medication.GenericDrugNameCode,
                        medication.GenericDrugNameCodeDesc,
                        medication.MedicineDisplayName,
                        medication.MedicineNameSaving,
                        medication.Comments
                    );
                if (medicalInfoResult.IsFailure)
                    throw new Exception(medicalInfoResult.Error);

                var rxInfoResult = PatientMedicationRXInfo.Create(
                        medication.Directions,
                        medication.Frequency,
                        null,
                        medication.Route,
                        medication.Quantity,
                        medication.Dosage,
                        medication.DoseFormId,
                        medication.DoseFormDesc,
                        medication.NumberOfRefillsAllowed,
                        medication.NumberOfRefillsRemaining,
                        medication.NextRefillDate != null ? DateOnly.FromDateTime(medication.NextRefillDate.Value) : null,
                        medication.StartDate != null ? DateOnly.FromDateTime(medication.StartDate.Value) : null,
                        medication.EndDate != null ? DateOnly.FromDateTime(medication.EndDate.Value) : null,
                        null,
                        null,
                        medication.Iscycle,
                        medication.Isdaw,
                        medication.Isprn
                    );
                if (rxInfoResult.IsFailure)
                    throw new Exception(rxInfoResult.Error);

                var newAdminHours = new HashSet<Domain.Entities.PatientMedicationAdminHour>();
                foreach (var adminHour in medication.AdminHours)
                {
                    var parsedHour = TimeOnly.ParseExact(adminHour.Hour, "hh:mm tt", CultureInfo.InvariantCulture);

                    newAdminHours.Add(new Domain.Entities.PatientMedicationAdminHour()
                    {
                        Hour = parsedHour
                    });
                }

                var newMedicationResult = await Domain.Entities.PatientMedication.Create(newPatientFacilityId, medication.OrderNumber, medicalInfoResult.Value, rxInfoResult.Value, newAdminHours, newPatient, null);
                if (newMedicationResult.IsFailure)
                    throw new Exception(newMedicationResult.Error);

                var newMediation = newMedicationResult.Value;

                if (medication.PatientMedicationStatusId == (int)EPatientMedicationStatus.OnHold)
                {
                    var changeStatusResult = newMediation.ChangeStatusMigration(Domain.Enums.EnumsCollection.EPatientMedicationStatus.OnHold, medication.DiscontinuationReason, newPatient);
                    if (changeStatusResult.IsFailure)
                        throw new Exception(changeStatusResult.Error);
                }

                //shadow properties
                if (medication.CreateDate != null)
                    newMediation.SetCreatedDate(medication.CreateDate.Value);
                if (medication.UpdateDate != null)
                    newMediation.SetUpdatedDate(medication.UpdateDate.Value);

                var addMedicationResult = await newPatient.AddMedicationForMigration(newMediation, newPatientFacilityId, _facilityClient);
            }

            //shadow properties
            if (patient.CreateDate != null)
                newPatient.SetCreatedDate(patient.CreateDate.Value);
            if(patient.UpdateDate != null)
                newPatient.SetUpdatedDate(patient.UpdateDate.Value);
            if(patient.UpdateBy != null)
                newPatient.SetUpdatedBy(patient.UpdateBy.Value);

            //_newPatientDbContext.Add(newPatient);
            //await _newPatientDbContext.SaveChangesAsync();

            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"Patient id : {patient.PatientId}, Error : {ex.Message}");
        }
    }
}